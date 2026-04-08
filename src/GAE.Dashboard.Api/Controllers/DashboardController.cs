using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Dashboard.Api.Security;
using GAE.Engine.Configuration;
using GAE.Engine.Data;
using GAE.Engine.Worlds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GAE.Dashboard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = DashboardPolicies.UserAccess)]
public class DashboardController : ControllerBase
{
    private readonly IStateManager _stateManager;
    private readonly IGameEngine _engine;
    private readonly IGameEventBroadcaster _broadcaster;
    private readonly INarratorService _narrator;
    private readonly IConversationLogger _conversationLogger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly WorldSeedConfig _worldSeedConfig;
    private readonly GameRulesConfig _rules;
    private readonly IContentRegistryService _registry;
    private readonly IDiscordNotifier? _discordNotifier;
    private readonly ContentSeedService? _contentSeed;
    private readonly IWorldContext _worldContext;
    private readonly IRealmTravelService _realmTravelService;
    private readonly IWorldRepository _worldRepository;
    private readonly IDbContextFactory<GaeDbContext>? _dbContextFactory;

    private static readonly TimeSpan NarratorCacheDuration = TimeSpan.FromSeconds(60);
    private static object? _narratorCachedResult;
    private static DateTimeOffset _narratorCacheExpiry;

    public DashboardController(
        IStateManager stateManager,
        IGameEngine engine,
        IGameEventBroadcaster broadcaster,
        INarratorService narrator,
        IConversationLogger conversationLogger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        WorldSeedConfig worldSeedConfig,
        GameRulesConfig rules,
        IContentRegistryService registry,
        IWorldContext worldContext,
        IRealmTravelService realmTravelService,
        IWorldRepository worldRepository,
        IDbContextFactory<GaeDbContext>? dbContextFactory = null,
        IDiscordNotifier? discordNotifier = null,
        ContentSeedService? contentSeed = null)
    {
        _stateManager = stateManager;
        _engine = engine;
        _broadcaster = broadcaster;
        _narrator = narrator;
        _conversationLogger = conversationLogger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _worldSeedConfig = worldSeedConfig;
        _rules = rules;
        _registry = registry;
        _worldContext = worldContext;
        _realmTravelService = realmTravelService;
        _worldRepository = worldRepository;
        _dbContextFactory = dbContextFactory;
        _discordNotifier = discordNotifier;
        _contentSeed = contentSeed;
    }

    [HttpGet("players")]
    public async Task<IActionResult> GetPlayers(CancellationToken ct)
    {
        var players = await _stateManager.GetAllPlayersAsync(ct);
        return Ok(players);
    }

    [HttpGet("players/{playerId}")]
    public async Task<IActionResult> GetPlayer(string playerId, CancellationToken ct)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        return player is not null ? Ok(player) : NotFound();
    }

    [HttpGet("rooms")]
    public async Task<IActionResult> GetRooms([FromQuery] string? worldId = null, CancellationToken ct = default)
    {
        var rooms = await _stateManager.GetAllRoomsAsync(ct);
        if (!string.IsNullOrWhiteSpace(worldId))
            return Ok(rooms.Where(r => r.WorldIds.Contains(worldId, StringComparer.OrdinalIgnoreCase)));
        return Ok(rooms);
    }

    [HttpGet("rooms/{roomId}")]
    public async Task<IActionResult> GetRoom(string roomId, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(roomId, ct);
        return room is not null ? Ok(room) : NotFound();
    }

    [HttpGet("story")]
    public async Task<IActionResult> GetStory([FromQuery] string? playerId = null, [FromQuery] string? worldId = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var entries = await _stateManager.GetStoryEntriesAsync(playerId, limit, ct);
        if (!string.IsNullOrWhiteSpace(worldId))
            return Ok(entries.Where(e => string.Equals(e.WorldId, worldId, StringComparison.OrdinalIgnoreCase)));
        return Ok(entries);
    }

    [HttpGet("story/room/{roomId}")]
    public async Task<IActionResult> GetRoomStory(string roomId, [FromQuery] string? worldId = null, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var resolvedWorldId = string.IsNullOrWhiteSpace(worldId) ? _worldContext.GetCurrentWorldOrDefault() : worldId;
        var entries = await _stateManager.GetRecentStoryForRoomAsync(roomId, resolvedWorldId, limit, ct);
        return Ok(entries);
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetDashboardHealth(CancellationToken ct)
    {
        var checks = new Dictionary<string, object?>
        {
            ["health"] = new
            {
                ok = true,
                status = "healthy",
                service = "core-api",
                timestamp = DateTimeOffset.UtcNow
            }
        };

        // Database health check
        if (_dbContextFactory is not null)
        {
            try
            {
                await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
                var canConnect = await db.Database.CanConnectAsync(ct);
                checks["health/db"] = canConnect
                    ? new { ok = true, status = "healthy", service = "postgres" }
                    : new { ok = false, status = "unhealthy", service = "postgres", note = "CanConnect returned false" };
            }
            catch (Exception ex)
            {
                checks["health/db"] = new { ok = false, status = "unhealthy", service = "postgres", error = ex.Message };
            }
        }

        if (_narratorCachedResult is not null && DateTimeOffset.UtcNow < _narratorCacheExpiry)
        {
            checks["health/narrator"] = _narratorCachedResult;
        }
        else
        {
            var narratorEndpoint = (_configuration["LmStudio:Endpoint"] ?? "http://localhost:1234").TrimEnd('/');

            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                using var response = await httpClient.GetAsync($"{narratorEndpoint}/v1/models", ct);
                _narratorCachedResult = response.IsSuccessStatusCode
                    ? new { ok = true, status = "healthy", service = "lm-studio" }
                    : new { ok = false, status = "degraded", service = "lm-studio", note = "Narration will use fallback text" };
            }
            catch (Exception ex)
            {
                _narratorCachedResult = new { ok = false, status = "degraded", service = "lm-studio", error = ex.Message, note = "Narration will use fallback text" };
            }

            _narratorCacheExpiry = DateTimeOffset.UtcNow + NarratorCacheDuration;
            checks["health/narrator"] = _narratorCachedResult;
        }

        return Ok(checks);
    }

    // ── LLM / Narrator model management ──

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/llm/models")]
    public async Task<IActionResult> ListModels(CancellationToken ct)
    {
        var available = await _narrator.ListAvailableModelsAsync(ct);
        var active = _narrator.GetActiveModel();
        return Ok(new { active, available });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/llm/model")]
    public IActionResult SetModel([FromBody] SetModelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
            return BadRequest(new { error = "model is required." });

        _narrator.SetActiveModel(request.Model);
        return Ok(new { active = _narrator.GetActiveModel(), summary = $"Model switched to {request.Model}." });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/transfer")]
    public async Task<IActionResult> TransferPlayerToWorld([FromBody] TransferPlayerWorldRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlayerId) || string.IsNullOrWhiteSpace(request.DestinationWorldId))
            return BadRequest(new { error = "playerId and destinationWorldId are required." });

        var transfer = await _realmTravelService.TransferPlayerAsync(
            request.PlayerId,
            request.DestinationWorldId,
            "admin-dashboard",
            ct: ct);

        if (!transfer.Success)
            return BadRequest(new { error = transfer.MechanicalSummary });

        await BroadcastAdminMutationAsync(
            summary: transfer.MechanicalSummary,
            playerId: request.PlayerId,
            data: new Dictionary<string, object?>
            {
                ["playerId"] = request.PlayerId,
                ["destinationWorldId"] = request.DestinationWorldId,
                ["mutation"] = "transfer-player-world"
            },
            ct: ct);

        return Ok(transfer);
    }

    public class SetModelRequest
    {
        public string Model { get; set; } = string.Empty;
    }

    public class TransferPlayerWorldRequest
    {
        public string PlayerId { get; set; } = string.Empty;
        public string DestinationWorldId { get; set; } = string.Empty;
    }

    [HttpPost("action")]
    public async Task<IActionResult> ProcessAction([FromBody] ActionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlayerId) || string.IsNullOrWhiteSpace(request.Command))
            return BadRequest(new { error = "playerId and command are required." });

        var player = await _stateManager.GetPlayerAsync(request.PlayerId, ct);
        if (player is not null)
            _worldContext.SetCurrentWorld(player.ActiveWorldId);

        var action = _engine.ParseCommand(request.PlayerId, request.Command);
        var result = await _engine.ProcessActionAsync(request.PlayerId, action, ct);

        // Mirror the result to the player's Discord thread (if they have one)
        if (_discordNotifier is not null)
        {
            try
            {
                var discordMsg = FormatActionForDiscord(request.Command, result);
                if (!string.IsNullOrEmpty(discordMsg))
                    await _discordNotifier.PostToPlayerThreadAsync(request.PlayerId, discordMsg, ct);
            }
            catch { /* Discord notification is best-effort */ }
        }

        return Ok(result);
    }

    private static string FormatActionForDiscord(string command, Core.Models.ActionResult result)
    {
        var sb = new System.Text.StringBuilder();

        // Header — show what command was run (from dashboard)
        sb.AppendLine($"📋 **[Dashboard]** `{command}`");

        // Mechanical outcome
        if (!string.IsNullOrWhiteSpace(result.MechanicalSummary))
        {
            var icon = result.Success ? "✅" : "❌";
            var summary = result.MechanicalSummary;
            // Truncate shop listings to keep Discord tidy
            if (summary.Length > 300)
                summary = summary[..297] + "...";
            sb.AppendLine($"{icon} {summary}");
        }

        // Dice rolls
        foreach (var roll in result.DiceRolls)
        {
            if (!string.IsNullOrWhiteSpace(roll.Purpose))
                sb.AppendLine($"🎲 {roll.Purpose} = {roll.Total} ({roll.Outcome})");
            else if (roll.Total > 0)
                sb.AppendLine($"🎲 Roll: {roll.Total} ({roll.Outcome})");
        }

        // Rewards
        if (result.GoldChange != 0)
            sb.AppendLine($"💰 Gold: {result.GoldChange:+#;-#}");
        if (result.XpGained > 0)
            sb.AppendLine($"⭐ XP: +{result.XpGained}");
        foreach (var item in result.ItemsGained)
            sb.AppendLine($"📦 Gained: {item.Name}");
        foreach (var item in result.ItemsLost)
            sb.AppendLine($"📤 Lost: {item.Name}");

        // Narration
        if (!string.IsNullOrWhiteSpace(result.Narration))
        {
            var narration = result.Narration.Length > 500
                ? result.Narration[..497] + "..."
                : result.Narration;
            sb.AppendLine($"\n*{narration}*");
        }

        return sb.ToString().TrimEnd();
    }

    [HttpPost("characters")]
    public async Task<IActionResult> CreateCharacter([FromBody] CreateCharacterRequest request, CancellationToken ct)
    {
        var validationError = ValidateCreateCharacterRequest(request);
        if (validationError is not null)
            return BadRequest(new { error = validationError });

        var playerId = string.IsNullOrWhiteSpace(request.PlayerId)
            ? Guid.NewGuid().ToString("N")
            : request.PlayerId.Trim();

        var existing = await _stateManager.GetPlayerAsync(playerId, ct);
        if (existing is not null)
            return Conflict(new { error = $"A player with id '{playerId}' already exists." });

        var concept = BuildCharacterConcept(request, playerId);
        var player = await _engine.CreateCharacterFromConceptAsync(concept, ct);
        return Ok(player);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/summary")]
    public async Task<IActionResult> GetAdminSummary(CancellationToken ct)
    {
        var players = await _stateManager.GetAllPlayersAsync(ct);
        var rooms = await _stateManager.GetAllRoomsAsync(ct);
        var storyEntries = await _stateManager.GetStoryEntriesAsync(limit: 500, ct: ct);
        var worlds = await _worldRepository.GetAllWorldsAsync(ct);
        var activeThreshold = DateTimeOffset.UtcNow.AddMinutes(-30);

        return Ok(new
        {
            playerCount = players.Count,
            activePlayerCount = players.Count(player => player.LastActiveAt >= activeThreshold),
            roomCount = rooms.Count,
            discoveredRoomCount = rooms.Count(room => room.IsDiscovered),
            storyEntryCount = storyEntries.Count,
            worldCount = worlds.Count,
            players = players
                .OrderBy(player => player.Name)
                .Select(player => new
                {
                    player.Id,
                    player.Name,
                    player.Race,
                    player.Class,
                    player.Level,
                    player.CurrentRoomId,
                    player.LastActiveAt
                }),
            rooms = rooms
                .OrderBy(room => room.Name)
                .Select(room => new
                {
                    room.Id,
                    room.Name,
                    room.IsDiscovered,
                    exitCount = room.Exits.Count,
                    npcCount = room.Npcs.Count,
                    itemCount = room.Items.Count
                })
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpDelete("admin/players/{playerId}")]
    public async Task<IActionResult> DeletePlayer(string playerId, CancellationToken ct)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{playerId}' was not found." });

        await _stateManager.RemovePlayerAsync(playerId, ct);

        await BroadcastAdminMutationAsync(
            summary: $"Deleted player {player.Name} ({playerId}).",
            playerId: playerId,
            data: new Dictionary<string, object?>
            {
                ["playerId"] = playerId,
                ["playerName"] = player.Name,
                ["mutation"] = "delete-player"
            },
            ct: ct);

        return Ok(new { summary = $"Deleted player {player.Name} ({playerId})." });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPut("admin/players/{playerId}")]
    public async Task<IActionResult> UpdatePlayer(string playerId, [FromBody] PlayerCharacter player, CancellationToken ct)
    {
        var existing = await _stateManager.GetPlayerAsync(playerId, ct);
        if (existing is null)
            return NotFound(new { error = $"Player '{playerId}' was not found." });

        player.Id = playerId; // Ensure ID matches route
        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);

        await BroadcastAdminMutationAsync(
            summary: $"Updated player {player.Name} ({playerId}).",
            playerId: playerId,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["mutation"] = "update-player"
            },
            ct: ct);

        return Ok(new { summary = $"Updated player {player.Name} ({playerId}).", player });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpDelete("admin/rooms/{roomId}")]
    public async Task<IActionResult> DeleteRoom(string roomId, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(roomId, ct);
        if (room is null)
            return NotFound(new { error = $"Room '{roomId}' was not found." });

        await _stateManager.RemoveRoomAsync(roomId, ct);

        await BroadcastAdminMutationAsync(
            summary: $"Deleted room {room.Name} ({roomId}).",
            playerId: null,
            data: new Dictionary<string, object?>
            {
                ["roomId"] = roomId,
                ["roomName"] = room.Name,
                ["mutation"] = "delete-room"
            },
            ct: ct);

        return Ok(new { summary = $"Deleted room {room.Name} ({roomId})." });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPut("admin/rooms/{roomId}")]
    public async Task<IActionResult> UpdateRoom(string roomId, [FromBody] Room room, CancellationToken ct)
    {
        var existing = await _stateManager.GetRoomAsync(roomId, ct);
        if (existing is null)
            return NotFound(new { error = $"Room '{roomId}' was not found." });

        room.Id = roomId; // Ensure ID matches route
        await _stateManager.SaveRoomAsync(room, ct);

        await BroadcastAdminMutationAsync(
            summary: $"Updated room {room.Name} ({roomId}).",
            playerId: null,
            data: new Dictionary<string, object?>
            {
                ["roomId"] = roomId,
                ["roomName"] = room.Name,
                ["mutation"] = "update-room"
            },
            ct: ct);

        return Ok(new { summary = $"Updated room {room.Name} ({roomId}).", room });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/rooms")]
    public async Task<IActionResult> CreateRoom([FromBody] Room room, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(room.Id))
            return BadRequest(new { error = "Room ID is required." });

        var existing = await _stateManager.GetRoomAsync(room.Id, ct);
        if (existing is not null)
            return Conflict(new { error = $"Room '{room.Id}' already exists." });

        await _stateManager.SaveRoomAsync(room, ct);

        await BroadcastAdminMutationAsync(
            summary: $"Created room {room.Name} ({room.Id}).",
            playerId: null,
            data: new Dictionary<string, object?>
            {
                ["roomId"] = room.Id,
                ["roomName"] = room.Name,
                ["mutation"] = "create-room"
            },
            ct: ct);

        return Ok(new { summary = $"Created room {room.Name} ({room.Id}).", room });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/seed-demo")]
    public async Task<IActionResult> SeedDemoCharacters([FromBody] SeedDemoCharactersRequest? request, CancellationToken ct)
    {
        var replaceExisting = request?.ReplaceExisting ?? false;
        var createdCount = 0;
        var seededPlayers = new List<PlayerCharacter>();

        foreach (var seed in GetDemoCharacterTemplates())
        {
            var existing = await _stateManager.GetPlayerAsync(seed.PlayerId!, ct);
            if (existing is not null && !replaceExisting)
            {
                seededPlayers.Add(existing);
                continue;
            }

            var player = await _engine.CreateCharacterFromConceptAsync(BuildCharacterConcept(seed, seed.PlayerId!), ct);
            seededPlayers.Add(player);
            createdCount++;
        }

        return Ok(new
        {
            createdCount,
            players = seededPlayers.OrderBy(player => player.Name)
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/reset-world")]
    public async Task<IActionResult> ResetWorld([FromBody] ResetWorldRequest? request, CancellationToken ct)
    {
        var keepPlayers = request?.KeepPlayers ?? true;
        var players = await _stateManager.GetAllPlayersAsync(ct);

        // 1. Nuke rooms, story, and combat
        await _stateManager.RemoveAllRoomsAsync(ct);
        await _stateManager.ClearStoryAsync(ct);
        await _stateManager.RemoveAllCombatStatesAsync(ct);

        // 2. Handle players
        if (keepPlayers)
        {
            // Reset players back to spawn with clean state, keep their identity
            foreach (var player in players)
            {
                ResetPlayerToConfiguredBaseline(player);
                await _stateManager.SavePlayerAsync(player, ct);
            }
        }
        else
        {
            // Delete all players
            foreach (var player in players)
                await _stateManager.RemovePlayerAsync(player.Id, ct);
        }

        // 3. Re-seed from lore-seed.yaml
        var roomsSeeded = 0;
        if (System.IO.File.Exists(_worldSeedConfig.LoreSeedPath))
        {
            var loreYaml = await System.IO.File.ReadAllTextAsync(_worldSeedConfig.LoreSeedPath, ct);
            var lore = _worldSeedConfig.YamlDeserializer.Deserialize<LoreSeed>(loreYaml);
            roomsSeeded = await SeedWorldFromLore(lore, _stateManager);
        }

        var summary = keepPlayers
            ? $"World reset. {roomsSeeded} rooms seeded. {players.Count} player(s) reset to spawn."
            : $"World reset. {roomsSeeded} rooms seeded. All players deleted.";

        await BroadcastAdminMutationAsync(
            summary: summary,
            data: new Dictionary<string, object?>
            {
                ["mutation"] = "reset-world",
                ["roomsSeeded"] = roomsSeeded,
                ["playersKept"] = keepPlayers,
                ["playerCount"] = players.Count
            },
            ct: ct);

        return Ok(new
        {
            summary,
            roomsSeeded,
            playersKept = keepPlayers,
            playerCount = players.Count
        });
    }

    private void ResetPlayerToConfiguredBaseline(PlayerCharacter player)
    {
        player.CurrentRoomId = "spawn";
        player.HasCompletedDemo = false;
        player.Inventory.Clear();
        player.Equipment = new EquipmentLoadout();
        player.StatusEffects.Clear();
        player.Spellbook.Clear();
        player.QuestLog.Clear();
        player.Interaction = new InteractionState();
        player.Gold = _rules.CharacterCreation.StartingGold;
        player.Xp = 0;
        player.Level = _rules.CharacterCreation.StartingLevel;

        AddDefaultHealSpell(player);

        foreach (var itemLookup in GetStartingItemLookups(player))
        {
            var template = ResolveItemTemplate(itemLookup);
            if (template is null)
                continue;

            var item = template.ToInventoryItem();
            if (item.IsEquippable)
            {
                var equippedSlot = player.Equipment.Equip(item, out _);
                if (equippedSlot is null)
                    AddItemToInventory(player, item);
            }
            else
            {
                AddItemToInventory(player, item);
            }
        }

        RecalculatePlayerResources(player);
        player.Hp = player.MaxHp;
        player.Mp = player.MaxMp;
        player.LastActiveAt = DateTimeOffset.UtcNow;
    }

    private List<string> GetStartingItemLookups(PlayerCharacter player)
    {
        var itemLookups = new List<string>();

        var classDefinition = _registry.Classes.GetAll().FirstOrDefault(candidate =>
            candidate.Id.Equals(player.Class, StringComparison.OrdinalIgnoreCase)
            || candidate.Name.Equals(player.Class, StringComparison.OrdinalIgnoreCase));

        if (classDefinition is not null)
            itemLookups.AddRange(classDefinition.StartingEquipment);

        itemLookups.AddRange(_rules.CharacterCreation.StartingItems);
        return itemLookups;
    }

    private ItemTemplate? ResolveItemTemplate(string itemLookup)
    {
        if (string.IsNullOrWhiteSpace(itemLookup))
            return null;

        return _registry.Items.GetAll().FirstOrDefault(candidate =>
            candidate.Id.Equals(itemLookup, StringComparison.OrdinalIgnoreCase)
            || candidate.Name.Equals(itemLookup, StringComparison.OrdinalIgnoreCase));
    }

    private void RecalculatePlayerResources(PlayerCharacter player)
    {
        int statMax = _rules.Stats.GetValueOrDefault("str")?.Max ?? 20;
        player.Str = Math.Clamp(player.Str, 1, statMax);
        player.Dex = Math.Clamp(player.Dex, 1, statMax);
        player.Con = Math.Clamp(player.Con, 1, statMax);
        player.Int = Math.Clamp(player.Int, 1, statMax);
        player.Wis = Math.Clamp(player.Wis, 1, statMax);
        player.Cha = Math.Clamp(player.Cha, 1, statMax);
        player.Luck = Math.Clamp(player.Luck, 1, statMax);

        int hpBase = _rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = _rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        int conMod = PlayerCharacter.GetStatModifier(player.Con);
        int intMod = PlayerCharacter.GetStatModifier(player.Int);
        int bonusLevels = Math.Max(0, player.Level - 1);

        int baseHp = hpBase + conMod;
        int baseMp = mpBase + intMod;
        player.MaxHp = Math.Max(1, (int)(baseHp * (1.0 + _rules.Leveling.HpScalePerLevel * bonusLevels)));
        player.MaxMp = Math.Max(0, (int)(baseMp * (1.0 + _rules.Leveling.MpScalePerLevel * bonusLevels)));
    }

    private static void AddDefaultHealSpell(PlayerCharacter player)
    {
        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Heal",
            Description = "Channel restorative magic to mend your wounds.",
            DamageDice = "1d4+2",
            DamageStat = "wis",
            Category = SpellCategory.Healing,
            MpCost = 2,
            BasePower = 1,
            LearnedAtLevel = 1,
            TargetType = "self"
        });
    }

    private static void AddItemToInventory(PlayerCharacter player, InventoryItem item)
    {
        var existing = player.Inventory.FirstOrDefault(candidate =>
            candidate.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)
            && candidate.Type == item.Type
            && !candidate.IsEquippable);

        if (existing is null)
        {
            player.Inventory.Add(item);
            return;
        }

        existing.Quantity += Math.Max(1, item.Quantity);
    }

    // SeedWorldFromLore is defined as a static method in Program.cs — call it via the same pattern
    private static async Task<int> SeedWorldFromLore(LoreSeed? lore, IStateManager stateManager)
    {
        if (lore is null) return 0;

        var allLoreRooms = new List<LoreRoom>();
        if (lore.StartingRoom is not null)
            allLoreRooms.Add(lore.StartingRoom);
        if (lore.Rooms is not null)
            allLoreRooms.AddRange(lore.Rooms);

        var count = 0;
        foreach (var loreRoom in allLoreRooms)
        {
            var room = ConvertLoreRoom(loreRoom);
            await stateManager.SaveRoomAsync(room);
            count++;
        }

        return count;
    }

    private static GAE.Core.Models.Room ConvertLoreRoom(LoreRoom lr)
    {
        var room = new GAE.Core.Models.Room
        {
            Id = lr.Id ?? Guid.NewGuid().ToString("N"),
            Name = lr.Name ?? "Unknown Room",
            Description = lr.Description ?? "An unremarkable room.",
            IsDiscovered = true,
            DiscoveredAt = DateTimeOffset.UtcNow
        };

        if (lr.Exits is not null)
            foreach (var exit in lr.Exits)
                room.Exits[exit.Key] = exit.Value;

        if (lr.Npcs is not null)
            room.Npcs.AddRange(lr.Npcs.Select(ConvertLoreNpc));

        if (lr.Items is not null)
            room.Items.AddRange(lr.Items.Select(ConvertLoreItem));

        if (lr.EnvironmentTags is not null)
            room.EnvironmentTags.AddRange(lr.EnvironmentTags);

        if (lr.WorldIds is { Count: > 0 })
        {
            room.WorldIds.Clear();
            room.WorldIds.AddRange(lr.WorldIds);
        }

        return room;
    }

    private static GAE.Core.Models.Npc ConvertLoreNpc(LoreNpc n)
    {
        var npc = new GAE.Core.Models.Npc
        {
            Id = n.Id ?? Guid.NewGuid().ToString("N"),
            Name = n.Name ?? "Unknown",
            Personality = n.Personality ?? "",
            Faction = n.Faction ?? "neutral",
            IsHostile = n.IsHostile,
            IsShopkeeper = n.IsShopkeeper,
            Level = n.Level ?? 1,
            Hp = n.Hp,
            MaxHp = n.MaxHp,
            AttackBonus = n.AttackBonus,
            DamageDice = n.DamageDice,
            Defense = n.Defense
        };

        if (n.KnowledgeScopes is not null)
            npc.KnowledgeScopes.AddRange(n.KnowledgeScopes);

        if (n.WorldIds is { Count: > 0 })
        {
            npc.WorldIds.Clear();
            npc.WorldIds.AddRange(n.WorldIds);
        }

        if (n.Loot is not null)
            npc.LootTable.AddRange(n.Loot.Select(ConvertLoreItem));

        if (n.ShopInventory is not null)
            npc.ShopInventory.AddRange(n.ShopInventory.Select(ConvertLoreItem));

        return npc;
    }

    private static GAE.Core.Models.InventoryItem ConvertLoreItem(LoreItem li)
    {
        var itemType = Enum.TryParse<ItemType>(li.Type, true, out var parsed)
            ? parsed
            : ItemType.Misc;

        return new InventoryItem
        {
            Id = li.Id ?? Guid.NewGuid().ToString("N"),
            Name = li.Name ?? "Unknown Item",
            Description = li.Description ?? "",
            Type = itemType,
            Quantity = Math.Max(1, li.Quantity),
            Value = Math.Max(0, li.Value),
            DamageDice = li.DamageDice,
            DamageStat = li.DamageStat,
            ArmorValue = Math.Max(0, li.ArmorValue),
            IsEquippable = li.IsEquippable ?? InventoryItem.IsEquippableType(itemType),
            IsConsumable = li.IsConsumable ?? itemType is ItemType.Potion or ItemType.Scroll,
            IsTwoHanded = li.IsTwoHanded,
            Effect = li.Effect,
            StatBonuses = li.StatBonuses ?? new()
        };
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/resources")]
    public async Task<IActionResult> AdjustResources([FromBody] AdjustResourcesRequest request, CancellationToken ct)
    {
        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        if (request.SetMaxHp.HasValue)
            player.MaxHp = Math.Max(1, request.SetMaxHp.Value);

        if (request.SetMaxMp.HasValue)
            player.MaxMp = Math.Max(0, request.SetMaxMp.Value);

        player.Hp = request.SetHp ?? player.Hp + request.HpDelta;
        player.Mp = request.SetMp ?? player.Mp + request.MpDelta;
        player.Gold = request.SetGold ?? player.Gold + request.GoldDelta;
        player.Xp = request.SetXp ?? player.Xp + request.XpDelta;
        player.Level = request.SetLevel ?? (player.Level + request.LevelDelta);

        player.Hp = Math.Clamp(player.Hp, 0, Math.Max(1, player.MaxHp));
        player.Mp = Math.Clamp(player.Mp, 0, Math.Max(0, player.MaxMp));
        player.Gold = Math.Max(0, player.Gold);
        player.Xp = Math.Max(0, player.Xp);
        player.Level = Math.Max(1, player.Level);

        // If XP was changed but level wasn't explicitly set, check for level-up
        if ((request.XpDelta != 0 || request.SetXp.HasValue) && !request.SetLevel.HasValue && request.LevelDelta == 0)
        {
            _engine.CheckAndApplyLevelUp(player);
        }

        player.LastActiveAt = DateTimeOffset.UtcNow;

        await _stateManager.SavePlayerAsync(player, ct);
        await BroadcastAdminMutationAsync(
            summary: $"Adjusted resources for {player.Name}.",
            playerId: player.Id,
            roomId: player.CurrentRoomId,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["mutation"] = "resources"
            },
            ct: ct);

        return Ok(new
        {
            summary = $"Adjusted resources for {player.Name}.",
            player
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/edit-player")]
    public async Task<IActionResult> EditPlayer([FromBody] EditPlayerRequest request, CancellationToken ct)
    {
        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        var changes = new List<string>();

        if (request.Name is not null) { player.Name = request.Name.Trim(); changes.Add("name"); }
        if (request.Race is not null) { player.Race = request.Race.Trim(); changes.Add("race"); }
        if (request.Class is not null) { player.Class = request.Class.Trim(); changes.Add("class"); }
        if (request.Backstory is not null) { player.Backstory = request.Backstory.Trim(); changes.Add("backstory"); }

        if (request.Hp.HasValue) { player.Hp = Math.Max(0, request.Hp.Value); changes.Add("hp"); }
        if (request.MaxHp.HasValue) { player.MaxHp = Math.Max(1, request.MaxHp.Value); changes.Add("maxHp"); }
        if (request.Mp.HasValue) { player.Mp = Math.Max(0, request.Mp.Value); changes.Add("mp"); }
        if (request.MaxMp.HasValue) { player.MaxMp = Math.Max(0, request.MaxMp.Value); changes.Add("maxMp"); }
        if (request.Gold.HasValue) { player.Gold = Math.Max(0, request.Gold.Value); changes.Add("gold"); }
        if (request.Xp.HasValue) { player.Xp = Math.Max(0, request.Xp.Value); changes.Add("xp"); }
        if (request.Level.HasValue) { player.Level = Math.Max(1, request.Level.Value); changes.Add("level"); }

        // No upper clamp — the DM should be able to set any stat value
        if (request.Str.HasValue) { player.Str = Math.Max(1, request.Str.Value); changes.Add("str"); }
        if (request.Dex.HasValue) { player.Dex = Math.Max(1, request.Dex.Value); changes.Add("dex"); }
        if (request.Con.HasValue) { player.Con = Math.Max(1, request.Con.Value); changes.Add("con"); }
        if (request.Int.HasValue) { player.Int = Math.Max(1, request.Int.Value); changes.Add("int"); }
        if (request.Wis.HasValue) { player.Wis = Math.Max(1, request.Wis.Value); changes.Add("wis"); }
        if (request.Cha.HasValue) { player.Cha = Math.Max(1, request.Cha.Value); changes.Add("cha"); }
        if (request.Luck.HasValue) { player.Luck = Math.Max(1, request.Luck.Value); changes.Add("luck"); }

        if (request.CurrentRoomId is not null)
        {
            var updatedRoomId = request.CurrentRoomId.Trim();
            ResetInteractionForForcedRoomChange(player, updatedRoomId);
            player.CurrentRoomId = updatedRoomId;
            changes.Add("room");
        }
        if (request.DiscordId is not null) { player.DiscordId = request.DiscordId.Trim(); changes.Add("discordId"); }
        if (request.ThreadId.HasValue) { player.ThreadId = request.ThreadId.Value == 0 ? null : request.ThreadId.Value; changes.Add("threadId"); }

        player.Hp = Math.Clamp(player.Hp, 0, Math.Max(1, player.MaxHp));
        player.Mp = Math.Clamp(player.Mp, 0, Math.Max(0, player.MaxMp));
        player.LastActiveAt = DateTimeOffset.UtcNow;

        await _stateManager.SavePlayerAsync(player, ct);
        var summary = $"Edited {player.Name}: {string.Join(", ", changes)}.";
        await BroadcastAdminMutationAsync(summary, playerId: player.Id, roomId: player.CurrentRoomId,
            data: new Dictionary<string, object?> { ["player"] = player, ["mutation"] = "edit-player", ["fields"] = changes }, ct: ct);

        return Ok(new { summary, player });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/send-message")]
    public async Task<IActionResult> SendPlayerMessage([FromBody] SendMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required." });

        if (_discordNotifier is null)
            return BadRequest(new { error = "Discord is not configured." });

        var msg = request.Message.Trim();
        int sent = 0;

        if (!string.IsNullOrWhiteSpace(request.PlayerId))
        {
            // Send to specific player
            var player = await RequirePlayerAsync(request.PlayerId, ct);
            if (player is null)
                return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

            await _discordNotifier.PostToPlayerThreadAsync(player.Id, msg, ct);
            sent = 1;
        }
        else
        {
            // Broadcast to all players
            var players = await _stateManager.GetAllPlayersAsync(ct);
            foreach (var p in players.Where(p => p.ThreadId.HasValue))
            {
                await _discordNotifier.PostToPlayerThreadAsync(p.Id, msg, ct);
                sent++;
            }
        }

        return Ok(new { sent, message = msg });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/teleport")]
    public async Task<IActionResult> TeleportPlayer([FromBody] TeleportPlayerRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            return BadRequest(new { error = "roomId is required." });

        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        var targetRoomId = request.RoomId.Trim();
        var previousRoom = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        var room = await _stateManager.GetRoomAsync(targetRoomId, ct);
        var createdRoom = false;

        if (room is null)
        {
            if (!request.CreateRoomIfMissing)
                return NotFound(new { error = $"Room '{targetRoomId}' was not found." });

            room = new Room
            {
                Id = targetRoomId,
                Name = string.IsNullOrWhiteSpace(request.RoomName) ? HumanizeIdentifier(targetRoomId) : request.RoomName.Trim(),
                Description = string.IsNullOrWhiteSpace(request.RoomDescription) ? "Admin-created test room." : request.RoomDescription.Trim(),
                IsDiscovered = true,
                DiscoveredAt = DateTimeOffset.UtcNow
            };
            createdRoom = true;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.RoomName))
                room.Name = request.RoomName.Trim();

            if (!string.IsNullOrWhiteSpace(request.RoomDescription))
                room.Description = request.RoomDescription.Trim();

            room.IsDiscovered = true;
            room.DiscoveredAt ??= DateTimeOffset.UtcNow;
        }

        foreach (var tag in NormalizeValues(request.EnvironmentTags))
        {
            if (!room.EnvironmentTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                room.EnvironmentTags.Add(tag);
        }

        if (request.ConnectFromCurrentRoom && previousRoom is not null && !string.Equals(previousRoom.Id, room.Id, StringComparison.OrdinalIgnoreCase))
        {
            var direction = string.IsNullOrWhiteSpace(request.EntryDirection) ? "debug" : request.EntryDirection.Trim().ToLowerInvariant();
            previousRoom.Exits[direction] = room.Id;
            room.Exits[GetOppositeDirection(direction)] = previousRoom.Id;
            await _stateManager.SaveRoomAsync(previousRoom, ct);
        }

        await _stateManager.SaveRoomAsync(room, ct);

        ResetInteractionForForcedRoomChange(player, room.Id);
        player.CurrentRoomId = room.Id;
        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);

        await BroadcastAdminMutationAsync(
            summary: createdRoom
                ? $"Created room {room.Name} and teleported {player.Name}."
                : $"Teleported {player.Name} to {room.Name}.",
            playerId: player.Id,
            roomId: room.Id,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["room"] = room,
                ["mutation"] = "teleport"
            },
            ct: ct);

        return Ok(new
        {
            summary = createdRoom
                ? $"Created room {room.Name} and teleported {player.Name}."
                : $"Teleported {player.Name} to {room.Name}.",
            player,
            room
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/grant-item")]
    public async Task<IActionResult> GrantItem([FromBody] GrantItemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required." });

        if (!Enum.TryParse<ItemType>(request.Type, true, out var itemType))
            return BadRequest(new { error = $"Unknown item type '{request.Type}'." });

        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        var item = new InventoryItem
        {
            Id = string.IsNullOrWhiteSpace(request.ItemId) ? Guid.NewGuid().ToString("N") : request.ItemId.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Type = itemType,
            Quantity = Math.Max(1, request.Quantity),
            Value = Math.Max(0, request.Value),
            DamageDice = string.IsNullOrWhiteSpace(request.DamageDice) ? null : request.DamageDice.Trim(),
            DamageStat = string.IsNullOrWhiteSpace(request.DamageStat) ? null : request.DamageStat.Trim(),
            ArmorValue = Math.Max(0, request.ArmorValue),
            IsConsumable = request.IsConsumable ?? itemType is ItemType.Potion or ItemType.Scroll,
            IsEquippable = request.IsEquippable ?? InventoryItem.IsEquippableType(itemType),
            IsTwoHanded = request.IsTwoHanded,
            Effect = string.IsNullOrWhiteSpace(request.Effect) ? null : request.Effect.Trim(),
            StatBonuses = request.StatBonuses ?? new()
        };

        string? equippedSlot = null;
        if (request.AutoEquip)
            equippedSlot = TryEquipItem(player, item);

        if (equippedSlot is null)
            player.Inventory.Add(item);

        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);

        var summary = equippedSlot is not null
            ? $"Granted {item.Name} to {player.Name} and equipped it in {equippedSlot}."
            : $"Granted {item.Name} to {player.Name}.";

        await BroadcastAdminMutationAsync(
            summary: summary,
            playerId: player.Id,
            roomId: player.CurrentRoomId,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["item"] = item,
                ["mutation"] = "grant-item"
            },
            ct: ct);

        return Ok(new
        {
            summary,
            player,
            item,
            equippedSlot
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/item-action")]
    public async Task<IActionResult> ApplyItemAction([FromBody] PlayerItemActionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
            return BadRequest(new { error = "itemId is required." });

        var action = request.Action.Trim().ToLowerInvariant();
        if (action is not ("remove" or "unequip"))
            return BadRequest(new { error = $"Unknown item action '{request.Action}'." });

        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        var itemId = request.ItemId.Trim();
        var inventoryItem = player.Inventory.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));

        InventoryItem? item;
        string source;
        string summary;
        string mutation;

        if (inventoryItem is not null)
        {
            if (action == "unequip")
                return BadRequest(new { error = $"Item '{inventoryItem.Name}' is already in inventory." });

            player.Inventory.Remove(inventoryItem);
            item = inventoryItem;
            source = "inventory";
            mutation = "remove-item";
            summary = $"Removed {item.Name} from {player.Name}'s inventory.";
        }
        else
        {
            item = player.Equipment.AllEquipped().FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
                return NotFound(new { error = $"Item '{request.ItemId}' was not found on player '{request.PlayerId}'." });

            if (!player.Equipment.Unequip(item))
                return Conflict(new { error = $"Unable to update item '{item.Name}'." });

            source = "equipment";
            if (action == "unequip")
            {
                player.Inventory.Add(item);
                mutation = "unequip-item";
                summary = $"Unequipped {item.Name} from {player.Name} and moved it to inventory.";
            }
            else
            {
                mutation = "remove-item";
                summary = $"Removed equipped {item.Name} from {player.Name}.";
            }
        }

        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);

        await BroadcastAdminMutationAsync(
            summary: summary,
            playerId: player.Id,
            roomId: player.CurrentRoomId,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["item"] = item,
                ["action"] = action,
                ["source"] = source,
                ["mutation"] = mutation
            },
            ct: ct);

        return Ok(new
        {
            summary,
            player,
            item,
            action,
            source
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/status")]
    public async Task<IActionResult> ApplyStatus([FromBody] ApplyStatusRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required." });

        if (!Enum.TryParse<StatusEffectType>(request.Type, true, out var statusType))
            return BadRequest(new { error = $"Unknown status type '{request.Type}'." });

        var modifiers = ParseStatModifiers(request.StatModifiers, request.StatModifiersText, out var modifierError);
        if (modifierError is not null)
            return BadRequest(new { error = modifierError });

        var player = await RequirePlayerAsync(request.PlayerId, ct);
        if (player is null)
            return NotFound(new { error = $"Player '{request.PlayerId}' was not found." });

        if (request.ReplaceExisting)
        {
            player.StatusEffects.RemoveAll(effect => string.Equals(effect.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var effect = new StatusEffect
        {
            Id = string.IsNullOrWhiteSpace(request.StatusId) ? Guid.NewGuid().ToString("N") : request.StatusId.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Type = statusType,
            RemainingTurns = Math.Max(1, request.RemainingTurns),
            DamagePerTurn = request.DamagePerTurn > 0 ? request.DamagePerTurn : null,
            HealPerTurn = request.HealPerTurn > 0 ? request.HealPerTurn : null,
            StatModifiers = modifiers
        };

        player.StatusEffects.Add(effect);
        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);

        var summary = $"Applied {effect.Name} to {player.Name}.";

        await BroadcastAdminMutationAsync(
            summary: summary,
            playerId: player.Id,
            roomId: player.CurrentRoomId,
            data: new Dictionary<string, object?>
            {
                ["player"] = player,
                ["statusEffect"] = effect,
                ["mutation"] = "status"
            },
            ct: ct);

        return Ok(new
        {
            summary,
            player,
            statusEffect = effect
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/mutations/room-fixture")]
    public async Task<IActionResult> UpsertRoomFixture([FromBody] UpsertRoomFixtureRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            return BadRequest(new { error = "roomId is required." });

        var roomId = request.RoomId.Trim();
        var room = await _stateManager.GetRoomAsync(roomId, ct);
        var created = room is null;

        room ??= new Room
        {
            Id = roomId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? HumanizeIdentifier(roomId) : request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? "Admin-created test fixture room." : request.Description.Trim(),
            IsDiscovered = request.IsDiscovered ?? true,
            DiscoveredAt = DateTimeOffset.UtcNow
        };

        if (!string.IsNullOrWhiteSpace(request.Name))
            room.Name = request.Name.Trim();

        if (!string.IsNullOrWhiteSpace(request.Description))
            room.Description = request.Description.Trim();

        if (request.IsDiscovered.HasValue)
            room.IsDiscovered = request.IsDiscovered.Value;

        room.DiscoveredAt ??= DateTimeOffset.UtcNow;

        if (request.ClearItems)
            room.Items.Clear();

        if (request.ClearNpcs)
            room.Npcs.Clear();

        if (request.ClearTags)
            room.EnvironmentTags.Clear();

        foreach (var tag in NormalizeValues(request.EnvironmentTags))
        {
            if (!room.EnvironmentTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                room.EnvironmentTags.Add(tag);
        }

        foreach (var exit in request.Exits ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(exit.Key) || string.IsNullOrWhiteSpace(exit.Value))
                continue;

            room.Exits[exit.Key.Trim().ToLowerInvariant()] = exit.Value.Trim();
        }

        foreach (var itemRequest in request.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(itemRequest.Name))
                continue;

            var itemName = itemRequest.Name.Trim();
            if (room.Items.Any(i => string.Equals(i.Name, itemName, StringComparison.OrdinalIgnoreCase)))
                continue;

            room.Items.Add(BuildRoomItem(itemRequest));
        }

        foreach (var npcRequest in request.Npcs ?? [])
        {
            if (string.IsNullOrWhiteSpace(npcRequest.Name))
                continue;

            var npcName = npcRequest.Name.Trim();
            if (room.Npcs.Any(n => string.Equals(n.Name, npcName, StringComparison.OrdinalIgnoreCase)))
                continue;

            room.Npcs.Add(new Npc
            {
                Id = string.IsNullOrWhiteSpace(npcRequest.NpcId) ? Guid.NewGuid().ToString("N") : npcRequest.NpcId.Trim(),
                Name = npcName,
                Personality = npcRequest.Personality?.Trim() ?? "Test fixture",
                Faction = string.IsNullOrWhiteSpace(npcRequest.Faction) ? "neutral" : npcRequest.Faction.Trim(),
                IsHostile = npcRequest.IsHostile,
                Hp = npcRequest.Hp,
                MaxHp = npcRequest.MaxHp ?? npcRequest.Hp,
                AttackBonus = npcRequest.AttackBonus,
                DamageDice = npcRequest.DamageDice,
                Defense = npcRequest.Defense,
                Level = npcRequest.Level,
                KnowledgeScopes = npcRequest.KnowledgeScopes ?? []
            });
        }

        await _stateManager.SaveRoomAsync(room, ct);
        await BroadcastAdminMutationAsync(
            summary: created ? $"Created room fixture {room.Name}." : $"Updated room fixture {room.Name}.",
            roomId: room.Id,
            data: new Dictionary<string, object?>
            {
                ["room"] = room,
                ["mutation"] = "room-fixture"
            },
            ct: ct);

        return Ok(new
        {
            summary = created ? $"Created room fixture {room.Name}." : $"Updated room fixture {room.Name}.",
            room
        });
    }

    private static void ResetInteractionForForcedRoomChange(PlayerCharacter player, string targetRoomId)
    {
        if (!string.Equals(player.CurrentRoomId, targetRoomId, StringComparison.OrdinalIgnoreCase))
            player.Interaction.Reset();
    }

    private static string? ValidateCreateCharacterRequest(CreateCharacterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "name is required.";

        if (string.IsNullOrWhiteSpace(request.Race))
            return "race is required.";

        if (string.IsNullOrWhiteSpace(request.Class))
            return "class is required.";

        return null;
    }

    private static CharacterConcept BuildCharacterConcept(CreateCharacterRequest request, string playerId)
    {
        return new CharacterConcept
        {
            PlayerDiscordId = playerId,
            Name = request.Name.Trim(),
            Race = request.Race.Trim(),
            Class = request.Class.Trim(),
            Backstory = request.Backstory?.Trim() ?? string.Empty,
            StatMethod = Enum.TryParse<StatAllocationMethod>(request.StatMethod, true, out var method)
                ? method
                : StatAllocationMethod.StandardArray
        };
    }

    // ── Conversation logs (training data) ──

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/conversations")]
    public async Task<IActionResult> GetConversationLogs(
        [FromQuery] string? operation = null,
        [FromQuery] string? playerId = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var logs = await _conversationLogger.GetLogsAsync(operation, playerId, limit, offset, ct);
        var total = await _conversationLogger.CountAsync(operation, playerId, ct);
        return Ok(new { total, offset, limit, logs });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/conversations/export")]
    public async Task ExportConversationLogs(CancellationToken ct)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"conversations.jsonl\"";
        await _conversationLogger.ExportJsonlAsync(Response.Body, ct);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/conversations/stats")]
    public async Task<IActionResult> GetConversationStats(CancellationToken ct)
    {
        var all = await _conversationLogger.GetLogsAsync(limit: 10000, ct: ct);
        var byOperation = all
            .GroupBy(l => l.Operation)
            .Select(g => new
            {
                operation = g.Key,
                count = g.Count(),
                avgLatencyMs = (int)g.Average(l => l.LatencyMs),
                errorCount = g.Count(l => !l.Success)
            })
            .OrderByDescending(x => x.count)
            .ToList();

        return Ok(new
        {
            totalExchanges = all.Count,
            byOperation,
            uniquePlayers = all.Where(l => l.PlayerId != null).Select(l => l.PlayerId).Distinct().Count(),
            avgLatencyMs = all.Count > 0 ? (int)all.Average(l => l.LatencyMs) : 0,
            errorRate = all.Count > 0 ? Math.Round(100.0 * all.Count(l => !l.Success) / all.Count, 1) : 0
        });
    }

    private async Task<PlayerCharacter?> RequirePlayerAsync(string playerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return null;

        return await _stateManager.GetPlayerAsync(playerId.Trim(), ct);
    }

    private async Task BroadcastAdminMutationAsync(
        string summary,
        string? playerId = null,
        string? roomId = null,
        Dictionary<string, object?>? data = null,
        CancellationToken ct = default)
    {
        await _broadcaster.BroadcastEventAsync(new GameEvent
        {
            Type = GameEventType.RoomUpdated,
            PlayerId = playerId ?? string.Empty,
            RoomId = roomId,
            Summary = summary,
            Narration = summary,
            Data = data ?? new Dictionary<string, object?>()
        }, ct);
    }

    private static InventoryItem BuildRoomItem(RoomFixtureItemRequest request)
    {
        var itemType = Enum.TryParse<ItemType>(request.Type, true, out var parsedType)
            ? parsedType
            : ItemType.Misc;
        return new InventoryItem
        {
            Id = string.IsNullOrWhiteSpace(request.ItemId) ? Guid.NewGuid().ToString("N") : request.ItemId.Trim(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Type = itemType,
            Quantity = Math.Max(1, request.Quantity),
            Value = Math.Max(0, request.Value),
            IsConsumable = request.IsConsumable ?? itemType is ItemType.Potion or ItemType.Scroll,
            IsEquippable = request.IsEquippable ?? InventoryItem.IsEquippableType(itemType)
        };
    }

    private static Dictionary<string, int> ParseStatModifiers(Dictionary<string, int>? supplied, string? text, out string? error)
    {
        error = null;
        var values = supplied is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(supplied, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
            return values;

        foreach (var segment in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || !int.TryParse(parts[1], out var modifier))
            {
                error = $"Unable to parse stat modifier '{segment}'. Use key:value pairs such as str:2,dex:-1.";
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            values[parts[0].ToLowerInvariant()] = modifier;
        }

        return values;
    }

    private static IEnumerable<string> NormalizeValues(IEnumerable<string>? values)
    {
        return values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            ?? [];
    }

    private static string HumanizeIdentifier(string identifier)
    {
        return string.Join(' ', identifier
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant()));
    }

    private static string GetOppositeDirection(string direction)
    {
        return direction switch
        {
            "north" => "south",
            "south" => "north",
            "east" => "west",
            "west" => "east",
            "up" => "down",
            "down" => "up",
            _ => "return"
        };
    }

    private static string? TryEquipItem(PlayerCharacter player, InventoryItem item)
    {
        var slotName = player.Equipment.Equip(item, out var displaced);
        foreach (var old in displaced)
            player.Inventory.Add(old);
        return slotName;
    }

    private static IEnumerable<CreateCharacterRequest> GetDemoCharacterTemplates()
    {
        yield return new CreateCharacterRequest
        {
            PlayerId = "demo-user",
            Name = "Ari Quickstep",
            Race = "Human",
            Class = "Ranger",
            Backstory = "A field scout used to validating journeys before the rest of the party commits.",
            StatMethod = "StandardArray"
        };

        yield return new CreateCharacterRequest
        {
            PlayerId = "demo-admin",
            Name = "Marshal Vale",
            Race = "Elf",
            Class = "Mage",
            Backstory = "The overseer who stress-tests encounters, world state, and operational flows before launch.",
            StatMethod = "StandardArray"
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  REGISTRY ENDPOINTS — browse, edit, generate content
    // ═══════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════
    //  DM CONSOLE — unified search across all game content
    // ═══════════════════════════════════════════════════════════════════

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/dm/search")]
    public async Task<IActionResult> DmSearch([FromQuery] string q, [FromQuery] string? type, [FromQuery] string? worldId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { results = Array.Empty<object>() });

        var query = q.Trim().ToLowerInvariant();
        var filter = type?.Trim().ToLowerInvariant();
        var wf = string.IsNullOrWhiteSpace(worldId) ? null : worldId.Trim();
        var results = new List<object>();

        bool InWorld(List<string>? wids) => wf is null || (wids?.Contains(wf, StringComparer.OrdinalIgnoreCase) ?? false);

        // Search spells
        if (filter is null or "spell")
            foreach (var s in _registry.Spells.GetAll())
                if (InWorld(s.WorldIds) && Matches(s.Id, s.Name, s.Description, s.School, s.Tags, query))
                    results.Add(new { type = "spell", s.Id, s.Name, s.Description, meta = $"{s.School} | Power {s.PowerLevel} | {s.ManaCost} MP", worldIds = s.WorldIds, data = s });

        // Search items
        if (filter is null or "item")
            foreach (var i in _registry.Items.GetAll())
                if (InWorld(i.WorldIds) && Matches(i.Id, i.Name, i.Description, i.Rarity, i.Tags, query))
                    results.Add(new { type = "item", i.Id, i.Name, i.Description, meta = $"{i.Type} | {i.Rarity} | {i.Value}g", worldIds = i.WorldIds, data = i });

        // Search classes
        if (filter is null or "class")
            foreach (var c in _registry.Classes.GetAll())
                if (InWorld(c.WorldIds) && Matches(c.Id, c.Name, c.Description, null, c.Tags, query))
                    results.Add(new { type = "class", c.Id, c.Name, c.Description, meta = $"{c.HitDie} | {c.PrimaryStat}{(c.CanCastSpells ? " | Caster" : "")}", worldIds = c.WorldIds, data = c });

        // Search races
        if (filter is null or "race")
            foreach (var r in _registry.Races.GetAll())
                if (InWorld(r.WorldIds) && Matches(r.Id, r.Name, r.Description, null, r.Tags, query))
                    results.Add(new { type = "race", r.Id, r.Name, r.Description, meta = string.Join(", ", r.Traits), worldIds = r.WorldIds, data = r });

        // Search monsters
        if (filter is null or "monster")
            foreach (var m in _registry.Monsters.GetAll())
                if (InWorld(m.WorldIds) && Matches(m.Id, m.Name, m.Description, m.Rarity, m.Tags, query))
                    results.Add(new { type = "monster", m.Id, m.Name, m.Description, meta = $"Lv.{m.MinLevel}-{m.MaxLevel} | HP {m.BaseHp} | {m.DamageDice} | {m.Rarity}{(m.IsBoss ? " | BOSS" : "")}", worldIds = m.WorldIds, data = m });

        // Search quests
        if (filter is null or "quest")
            foreach (var qst in _registry.Quests.GetAll())
                if (InWorld(qst.WorldIds) && Matches(qst.Id, qst.Name, qst.Description, qst.GiverId, qst.Tags, query))
                    results.Add(new { type = "quest", qst.Id, qst.Name, qst.Description, meta = $"Lv.{qst.MinLevel} | {qst.Stages.Count} stages | Giver: {qst.GiverId}", worldIds = qst.WorldIds, data = qst });

        // Search lore entries
        if (filter is null or "lore_entry")
            foreach (var l in _registry.LoreEntries.GetAll())
                if (InWorld(l.WorldIds) && Matches(l.Id, l.Name, l.Description, l.LoreScope, l.Tags, query))
                    results.Add(new { type = "lore_entry", l.Id, l.Name, l.Description, meta = $"{l.LoreScope} | {(l.IsStarterLore ? "Starter" : l.DiscoveryTrigger)}{(l.CascadeDown ? " | Cascades" : "")}{(l.ParentLoreId is not null ? $" | Parent: {l.ParentLoreId}" : "")}", worldIds = l.WorldIds, data = l });

        // Search narrator presets
        if (filter is null or "narrator_preset")
            foreach (var np in _registry.NarratorPresets.GetAll())
                if (InWorld(np.WorldIds) && Matches(np.Id, np.Name, np.Description, np.Archetype, np.Tags, query))
                    results.Add(new { type = "narrator_preset", np.Id, np.Name, np.Description, meta = $"{np.Archetype}{(np.IsSelectable ? "" : " | Admin-only")} | Order: {np.SortOrder}", worldIds = np.WorldIds, data = np });

        // Fetch rooms once if needed for room or NPC search
        var needRooms = filter is null or "room" or "npc";
        var rooms = needRooms ? await _stateManager.GetAllRoomsAsync(ct) : [];

        // Search rooms
        if (filter is null or "room")
            foreach (var rm in rooms)
                if (InWorld(rm.WorldIds) &&
                    (rm.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    rm.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (rm.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
                    results.Add(new { type = "room", id = rm.Id, name = rm.Name, description = rm.Description,
                        meta = $"{rm.Npcs.Count} NPCs | {rm.Items.Count} items | {rm.Exits.Count} exits", worldIds = rm.WorldIds, data = rm });

        // Search players
        if (filter is null or "player")
        {
            var players = await _stateManager.GetAllPlayersAsync(ct);
            foreach (var p in players)
                if ((wf is null || string.Equals(p.ActiveWorldId, wf, StringComparison.OrdinalIgnoreCase)) &&
                    (p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Race.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Class.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    results.Add(new { type = "player", id = p.Id, name = p.Name,
                        description = $"Lv.{p.Level} {p.Race} {p.Class}",
                        meta = $"HP: {p.Hp}/{p.MaxHp} | MP: {p.Mp}/{p.MaxMp} | Gold: {p.Gold}", worldIds = new List<string> { p.ActiveWorldId }, data = p });
        }

        // Search NPCs (across all rooms)
        if (filter is null or "npc")
            foreach (var rm in rooms)
                foreach (var npc in rm.Npcs)
                    if (InWorld(npc.WorldIds) &&
                        (npc.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        npc.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (npc.Personality?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        npc.Faction.Contains(query, StringComparison.OrdinalIgnoreCase)))
                        results.Add(new { type = "npc", id = npc.Id, name = npc.Name,
                            description = npc.Personality,
                            meta = $"{npc.Faction} | Lv.{npc.Level}{(npc.IsHostile ? " | Hostile" : "")} | Room: {rm.Name}", worldIds = npc.WorldIds, data = npc, roomId = rm.Id });

        return Ok(new { results, total = results.Count });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/dm/browse/{type}")]
    public async Task<IActionResult> DmBrowse(string type, [FromQuery] string? worldId, CancellationToken ct)
    {
        var results = new List<object>();
        var wf = string.IsNullOrWhiteSpace(worldId) ? null : worldId.Trim();
        bool InWorld(List<string>? wids) => wf is null || (wids?.Contains(wf, StringComparer.OrdinalIgnoreCase) ?? false);

        switch (type.ToLowerInvariant())
        {
            case "spells":
                foreach (var s in _registry.Spells.GetAll())
                    if (InWorld(s.WorldIds))
                        results.Add(new { type = "spell", s.Id, s.Name, s.Description, meta = $"{s.School} | Power {s.PowerLevel} | {s.ManaCost} MP", worldIds = s.WorldIds, data = s });
                break;
            case "items":
                foreach (var i in _registry.Items.GetAll())
                    if (InWorld(i.WorldIds))
                        results.Add(new { type = "item", i.Id, i.Name, i.Description, meta = $"{i.Type} | {i.Rarity} | {i.Value}g", worldIds = i.WorldIds, data = i });
                break;
            case "classes":
                foreach (var c in _registry.Classes.GetAll())
                    if (InWorld(c.WorldIds))
                        results.Add(new { type = "class", c.Id, c.Name, c.Description, meta = $"{c.HitDie} | {c.PrimaryStat}{(c.CanCastSpells ? " | Caster" : "")}", worldIds = c.WorldIds, data = c });
                break;
            case "races":
                foreach (var r in _registry.Races.GetAll())
                    if (InWorld(r.WorldIds))
                        results.Add(new { type = "race", r.Id, r.Name, r.Description, meta = string.Join(", ", r.Traits), worldIds = r.WorldIds, data = r });
                break;
            case "rooms":
                foreach (var rm in await _stateManager.GetAllRoomsAsync(ct))
                    if (InWorld(rm.WorldIds))
                        results.Add(new { type = "room", id = rm.Id, name = rm.Name, description = rm.Description,
                            meta = $"{rm.Npcs.Count} NPCs | {rm.Items.Count} items | {rm.Exits.Count} exits", worldIds = rm.WorldIds, data = rm });
                break;
            case "players":
                foreach (var p in await _stateManager.GetAllPlayersAsync(ct))
                    if (wf is null || string.Equals(p.ActiveWorldId, wf, StringComparison.OrdinalIgnoreCase))
                        results.Add(new { type = "player", id = p.Id, name = p.Name,
                            description = $"Lv.{p.Level} {p.Race} {p.Class}",
                            meta = $"HP: {p.Hp}/{p.MaxHp} | MP: {p.Mp}/{p.MaxMp} | Gold: {p.Gold}", worldIds = new List<string> { p.ActiveWorldId }, data = p });
                break;
            case "monsters":
                foreach (var m in _registry.Monsters.GetAll())
                    if (InWorld(m.WorldIds))
                        results.Add(new { type = "monster", m.Id, m.Name, m.Description,
                            meta = $"Lv.{m.MinLevel}-{m.MaxLevel} | HP {m.BaseHp} | {m.DamageDice} | {m.Rarity}{(m.IsBoss ? " | BOSS" : "")}", worldIds = m.WorldIds, data = m });
                break;
            case "quests":
                foreach (var qst in _registry.Quests.GetAll())
                    if (InWorld(qst.WorldIds))
                        results.Add(new { type = "quest", qst.Id, qst.Name, qst.Description,
                            meta = $"Lv.{qst.MinLevel} | {qst.Stages.Count} stages | Giver: {qst.GiverId}", worldIds = qst.WorldIds, data = qst });
                break;
            case "lore_entries":
                foreach (var l in _registry.LoreEntries.GetAll())
                    if (InWorld(l.WorldIds))
                        results.Add(new { type = "lore_entry", l.Id, l.Name, l.Description,
                            meta = $"{l.LoreScope} | {(l.IsStarterLore ? "Starter" : l.DiscoveryTrigger)}{(l.CascadeDown ? " | Cascades" : "")}{(l.ParentLoreId is not null ? $" | Parent: {l.ParentLoreId}" : "")}", worldIds = l.WorldIds, data = l });
                break;
            case "narrator_presets":
                foreach (var np in _registry.NarratorPresets.GetAll())
                    if (InWorld(np.WorldIds))
                        results.Add(new { type = "narrator_preset", np.Id, np.Name, np.Description,
                            meta = $"{np.Archetype}{(np.IsSelectable ? "" : " | Admin-only")} | Order: {np.SortOrder}", worldIds = np.WorldIds, data = np });
                break;
        }
        return Ok(new { results, total = results.Count });
    }

    private static bool Matches(string id, string name, string? desc, string? extra, List<string>? tags, string query)
    {
        if (id.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (desc?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) return true;
        if (extra?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) return true;
        if (tags?.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false) return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  REGISTRY ENDPOINTS — CRUD for all game content
    // ═══════════════════════════════════════════════════════════════════

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/registry/{type}")]
    public IActionResult GetRegistry(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "spells" => Ok(_registry.Spells.GetAll()),
            "classes" => Ok(_registry.Classes.GetAll()),
            "races" => Ok(_registry.Races.GetAll()),
            "items" => Ok(_registry.Items.GetAll()),
            "monsters" => Ok(_registry.Monsters.GetAll()),
            "quests" => Ok(_registry.Quests.GetAll()),
            "lore_entries" => Ok(_registry.LoreEntries.GetAll()),
            "narrator_presets" => Ok(_registry.NarratorPresets.GetAll()),
            _ => NotFound(new { error = $"Unknown registry type: {type}" })
        };
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/registry/{type}/{id}")]
    public IActionResult GetRegistryEntry(string type, string id)
    {
        object? entry = type.ToLowerInvariant() switch
        {
            "spells" => _registry.Spells.GetById(id),
            "classes" => _registry.Classes.GetById(id),
            "races" => _registry.Races.GetById(id),
            "items" => _registry.Items.GetById(id),
            "monsters" => _registry.Monsters.GetById(id),
            "quests" => _registry.Quests.GetById(id),
            "lore_entries" => _registry.LoreEntries.GetById(id),
            "narrator_presets" => _registry.NarratorPresets.GetById(id),
            _ => null
        };
        return entry is not null ? Ok(entry) : NotFound();
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/registry/summary")]
    public IActionResult GetRegistrySummary()
    {
        return Ok(new
        {
            spells = _registry.Spells.Count,
            classes = _registry.Classes.Count,
            races = _registry.Races.Count,
            items = _registry.Items.Count,
            loreEntries = _registry.LoreEntries.Count,
            narratorPresets = _registry.NarratorPresets.Count,
            spellList = _registry.Spells.GetAll().Select(s => new { s.Id, s.Name, s.School, s.PowerLevel, s.ManaCost, s.RequiredLevel }),
            classList = _registry.Classes.GetAll().Select(c => new { c.Id, c.Name, c.CanCastSpells, c.PrimaryStat }),
            raceList = _registry.Races.GetAll().Select(r => new { r.Id, r.Name, r.Traits }),
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/registry/{type}")]
    public async Task<IActionResult> UpsertRegistryEntry(string type, [FromBody] System.Text.Json.JsonElement body, CancellationToken ct)
    {
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            switch (type.ToLowerInvariant())
            {
                case "spells":
                    var spell = System.Text.Json.JsonSerializer.Deserialize<SpellDefinition>(body.GetRawText(), options);
                    if (spell is null || string.IsNullOrWhiteSpace(spell.Id)) return BadRequest(new { error = "Invalid spell data" });
                    _registry.Spells.Register(spell);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("spell", spell, ct);
                    return Ok(spell);
                case "classes":
                    var cls = System.Text.Json.JsonSerializer.Deserialize<ClassDefinition>(body.GetRawText(), options);
                    if (cls is null || string.IsNullOrWhiteSpace(cls.Id)) return BadRequest(new { error = "Invalid class data" });
                    _registry.Classes.Register(cls);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("class", cls, ct);
                    return Ok(cls);
                case "races":
                    var race = System.Text.Json.JsonSerializer.Deserialize<RaceDefinition>(body.GetRawText(), options);
                    if (race is null || string.IsNullOrWhiteSpace(race.Id)) return BadRequest(new { error = "Invalid race data" });
                    _registry.Races.Register(race);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("race", race, ct);
                    return Ok(race);
                case "items":
                    var item = System.Text.Json.JsonSerializer.Deserialize<ItemTemplate>(body.GetRawText(), options);
                    if (item is null || string.IsNullOrWhiteSpace(item.Id)) return BadRequest(new { error = "Invalid item data" });
                    _registry.Items.Register(item);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("item", item, ct);
                    return Ok(item);
                case "monsters":
                    var monster = System.Text.Json.JsonSerializer.Deserialize<MonsterTemplate>(body.GetRawText(), options);
                    if (monster is null || string.IsNullOrWhiteSpace(monster.Id)) return BadRequest(new { error = "Invalid monster data" });
                    _registry.Monsters.Register(monster);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("monster", monster, ct);
                    return Ok(monster);
                case "quests":
                    var quest = System.Text.Json.JsonSerializer.Deserialize<QuestDefinition>(body.GetRawText(), options);
                    if (quest is null || string.IsNullOrWhiteSpace(quest.Id)) return BadRequest(new { error = "Invalid quest data" });
                    _registry.Quests.Register(quest);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("quest", quest, ct);
                    return Ok(quest);
                case "lore_entries":
                    var loreEntry = System.Text.Json.JsonSerializer.Deserialize<LoreEntry>(body.GetRawText(), options);
                    if (loreEntry is null || string.IsNullOrWhiteSpace(loreEntry.Id)) return BadRequest(new { error = "Invalid lore entry data" });
                    _registry.LoreEntries.Register(loreEntry);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("lore_entry", loreEntry, ct);
                    return Ok(loreEntry);
                case "narrator_presets":
                    var narratorPreset = System.Text.Json.JsonSerializer.Deserialize<NarratorPreset>(body.GetRawText(), options);
                    if (narratorPreset is null || string.IsNullOrWhiteSpace(narratorPreset.Id)) return BadRequest(new { error = "Invalid narrator preset data" });
                    _registry.NarratorPresets.Register(narratorPreset);
                    if (_contentSeed is not null) await _contentSeed.SaveEntryAsync("narrator_preset", narratorPreset, ct);
                    return Ok(narratorPreset);
                default:
                    return NotFound(new { error = $"Unknown registry type: {type}" });
            }
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpDelete("admin/registry/{type}/{id}")]
    public async Task<IActionResult> DeleteRegistryEntry(string type, string id, CancellationToken ct)
    {
        string? contentType = null;
        switch (type.ToLowerInvariant())
        {
            case "spells": _registry.Spells.Remove(id); contentType = "spell"; break;
            case "classes": _registry.Classes.Remove(id); contentType = "class"; break;
            case "races": _registry.Races.Remove(id); contentType = "race"; break;
            case "items": _registry.Items.Remove(id); contentType = "item"; break;
            case "monsters": _registry.Monsters.Remove(id); contentType = "monster"; break;
            case "quests": _registry.Quests.Remove(id); contentType = "quest"; break;
            case "lore_entries": _registry.LoreEntries.Remove(id); contentType = "lore_entry"; break;
            case "narrator_presets": _registry.NarratorPresets.Remove(id); contentType = "narrator_preset"; break;
            default: return NotFound(new { error = $"Unknown registry type: {type}" });
        }
        if (_contentSeed is not null && contentType is not null)
            await _contentSeed.RemoveEntryAsync(contentType, id, ct);
        return Ok(new { success = true, id });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/registry/generate")]
    public async Task<IActionResult> GenerateContent([FromBody] ContentGenerateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ContentType) || string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new { error = "contentType and description are required." });

        var result = await _narrator.GenerateContentAsync(request.ContentType, request.Description, request.ExistingJson, ct);
        return Ok(new { json = result });
    }

    /// <summary>
    /// AI-assisted quest generation. Accepts a brief, selected lore context, and world ID.
    /// Assembles a rich prompt with relevant lore, NPCs, and locations, then returns a structured quest definition.
    /// </summary>
    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/registry/generate-quest")]
    public async Task<IActionResult> GenerateQuest([FromBody] GenerateQuestRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Brief))
            return BadRequest(new { error = "A brief description is required." });

        // Gather lore context
        var loreContext = new List<string>();
        if (request.LoreEntryIds is { Count: > 0 })
        {
            foreach (var loreId in request.LoreEntryIds)
            {
                var lore = _registry.LoreEntries.GetById(loreId);
                if (lore is not null)
                    loreContext.Add($"[{lore.LoreScope}: {lore.Name}] {lore.Content ?? lore.Description ?? ""}");
            }
        }

        // Gather NPC context from specified world
        var npcContext = new List<string>();
        var roomContext = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.WorldId))
        {
            var rooms = await _stateManager.GetAllRoomsAsync(ct);
            var worldRooms = rooms.Where(r => r.WorldIds?.Contains(request.WorldId, StringComparer.OrdinalIgnoreCase) ?? false).ToList();
            foreach (var rm in worldRooms)
            {
                roomContext.Add($"Room: {rm.Name} (id: {rm.Id}) - {rm.Description}");
                foreach (var npc in rm.Npcs)
                    npcContext.Add($"NPC: {npc.Name} (id: {npc.Id}) in {rm.Name} - {npc.Personality}, faction: {npc.Faction}");
            }
        }

        // Build the generation prompt with full context
        var contextBlock = "";
        if (loreContext.Count > 0)
            contextBlock += "\n\n## Relevant Lore\n" + string.Join("\n", loreContext);
        if (npcContext.Count > 0)
            contextBlock += "\n\n## Available NPCs\n" + string.Join("\n", npcContext.Take(30));
        if (roomContext.Count > 0)
            contextBlock += "\n\n## Available Locations\n" + string.Join("\n", roomContext.Take(30));

        // Existing quests for reference
        var existingQuests = _registry.Quests.GetAll()
            .Where(q => string.IsNullOrWhiteSpace(request.WorldId) || (q.WorldIds?.Contains(request.WorldId, StringComparer.OrdinalIgnoreCase) ?? false))
            .Select(q => $"- {q.Name} (id: {q.Id}): {q.Description}")
            .Take(20);
        if (existingQuests.Any())
            contextBlock += "\n\n## Existing Quests (avoid duplicates)\n" + string.Join("\n", existingQuests);

        var fullDescription = $"Generate a quest based on this brief: {request.Brief}\n\nTarget level range: {request.MinLevel ?? 1}-{request.MaxLevel ?? 5}{contextBlock}";

        var result = await _narrator.GenerateContentAsync("quest", fullDescription, null, ct);
        return Ok(new { json = result });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  WORLD MANAGEMENT ENDPOINTS
    // ═══════════════════════════════════════════════════════════════════

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/worlds")]
    public async Task<IActionResult> GetWorlds(CancellationToken ct)
    {
        var worlds = await _worldRepository.GetAllWorldsAsync(ct);
        var players = await _stateManager.GetAllPlayersAsync(ct);
        var result = worlds.Select(w => new
        {
            w.Id, w.Name, w.Description, w.SpawnRoomId, w.IsActive,
            w.Tags, w.CreatedBy, w.CreatedAt, w.UpdatedAt,
            w.CharacterCreationIntro, w.DefaultNarratorPresetId, w.NarratorPresetIds,
            portalCount = w.Portals.Count,
            playerCount = players.Count(p =>
                string.Equals(p.ActiveWorldId, w.Id, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrEmpty(p.ActiveWorldId) && string.Equals(w.Id, WorldDefaults.DefaultWorldId, StringComparison.OrdinalIgnoreCase))),
            statCount = w.Rules.Stats.Count(s =>
                !string.Equals(s.Value.Category, "resource", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.Value.Category, "currency", StringComparison.OrdinalIgnoreCase))
        });
        return Ok(result);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/worlds/{worldId}")]
    public async Task<IActionResult> GetWorld(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });
        return Ok(world);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds")]
    public async Task<IActionResult> CreateWorld([FromBody] CreateWorldRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "World name is required." });

        var id = request.Id?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            id = request.Name.Trim().ToLowerInvariant().Replace(' ', '_').Replace("'", "");

        var existing = await _worldRepository.GetWorldAsync(id, ct);
        if (existing is not null)
            return Conflict(new { error = $"World '{id}' already exists." });

        var world = new World
        {
            Id = id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            SpawnRoomId = request.SpawnRoomId?.Trim() ?? "spawn",
            IsActive = request.IsActive,
            Tags = request.Tags ?? [],
            CreatedBy = User.Identity?.Name ?? "admin",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        if (request.Rules is not null)
            world.Rules = request.Rules;

        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(world);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPut("admin/worlds/{worldId}")]
    public async Task<IActionResult> UpdateWorld(string worldId, [FromBody] UpdateWorldRequest request, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        if (!string.IsNullOrWhiteSpace(request.Name)) world.Name = request.Name.Trim();
        if (request.Description is not null) world.Description = request.Description.Trim();
        if (!string.IsNullOrWhiteSpace(request.SpawnRoomId)) world.SpawnRoomId = request.SpawnRoomId.Trim();
        if (request.IsActive.HasValue) world.IsActive = request.IsActive.Value;
        if (request.Tags is not null) world.Tags = request.Tags;
        if (request.Rules is not null) world.Rules = request.Rules;
        if (request.CharacterCreationIntro is not null) world.CharacterCreationIntro = string.IsNullOrWhiteSpace(request.CharacterCreationIntro) ? null : request.CharacterCreationIntro.Trim();
        if (request.DefaultNarratorPresetId is not null) world.DefaultNarratorPresetId = string.IsNullOrWhiteSpace(request.DefaultNarratorPresetId) ? null : request.DefaultNarratorPresetId.Trim();
        if (request.NarratorPresetIds is not null) world.NarratorPresetIds = request.NarratorPresetIds;
        world.UpdatedAt = DateTimeOffset.UtcNow;

        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(world);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/{worldId}/generate-intro")]
    public async Task<IActionResult> GenerateWorldIntro(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        // Gather starter lore for context
        var starterLore = _registry.LoreEntries.GetAll()
            .Where(l => l.IsStarterLore)
            .Select(l => $"- {l.Name}: {l.Content}")
            .ToList();

        var loreContext = starterLore.Count > 0
            ? "Key world lore:\n" + string.Join("\n", starterLore.Take(5))
            : "";

        // Gather main storyline quests for context
        var mainQuests = _registry.Quests.GetAll()
            .Where(q => q.Tags.Any(t => string.Equals(t, "main", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(t, "storyline", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(t, "crystal", StringComparison.OrdinalIgnoreCase)))
            .Select(q => $"- {q.Name}: {q.Description}")
            .ToList();

        var questContext = mainQuests.Count > 0
            ? "\nMain storyline quests (hint at but don't spoil):\n" + string.Join("\n", mainQuests.Take(5))
            : "";

        var prompt = $"""
            Generate a Discord character creation intro message for a text RPG world.
            The message should:
            1. Introduce the Narrator as a character (the voice that narrates the player's adventure)
            2. Hint at the world's setting and main conflict without spoiling specifics
            3. Ask the player to describe who they are
            4. End with the instruction in italics: *(Describe yourself however you like: "I'm a sneaky halfling who picks pockets" or "I'm a massive orc who solves problems with fists" — or just tell me your name and I'll ask questions.)*

            World name: {world.Name}
            World description: {world.Description}
            {loreContext}
            {questContext}

            Write the intro using Discord markdown (bold, italics, etc). Keep it 3-5 paragraphs.
            The Narrator should have personality — dry wit, slight amusement, world-weary wisdom.
            Return ONLY the message text, no JSON wrapping.
            """;

        try
        {
            var result = await _narrator.GenerateContentAsync("text", prompt, null, ct);
            return Ok(new { intro = result });
        }
        catch (Exception)
        {
            return StatusCode(503, new { error = "AI narrator unavailable. Write the intro manually or try again." });
        }
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpDelete("admin/worlds/{worldId}")]
    public async Task<IActionResult> DeleteWorld(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        if (string.Equals(worldId, WorldDefaults.DefaultWorldId, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Cannot delete the default world." });

        var players = await _stateManager.GetAllPlayersAsync(ct);
        var stuck = players.Where(p =>
            string.Equals(p.ActiveWorldId, worldId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (stuck.Count > 0)
            return Conflict(new
            {
                error = $"{stuck.Count} player(s) are still in this world. Transfer them first.",
                players = stuck.Select(p => new { p.Id, p.Name })
            });

        await _worldRepository.RemoveWorldAsync(worldId, ct);
        return Ok(new { success = true, id = worldId });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/{worldId}/activate")]
    public async Task<IActionResult> ActivateWorld(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });
        world.IsActive = true;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(new { success = true, id = worldId, isActive = true });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/{worldId}/deactivate")]
    public async Task<IActionResult> DeactivateWorld(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });
        world.IsActive = false;
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(new { success = true, id = worldId, isActive = false });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/worlds/{worldId}/players")]
    public async Task<IActionResult> GetWorldPlayers(string worldId, CancellationToken ct)
    {
        var players = await _stateManager.GetAllPlayersAsync(ct);
        var inWorld = players.Where(p =>
            string.Equals(p.ActiveWorldId, worldId, StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrEmpty(p.ActiveWorldId) && string.Equals(worldId, WorldDefaults.DefaultWorldId, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new
            {
                p.Id, p.Name, p.Class, p.Race, p.Level,
                p.Hp, p.MaxHp, p.Mp, p.MaxMp,
                p.CurrentRoomId, p.ActiveWorldId, p.HomeWorldId
            });
        return Ok(inWorld);
    }

    // ─── Portal Management ───

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/worlds/{worldId}/portals")]
    public async Task<IActionResult> GetWorldPortals(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });
        return Ok(world.Portals);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/{worldId}/portals")]
    public async Task<IActionResult> CreatePortal(string worldId, [FromBody] CreatePortalRequest request, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        if (string.IsNullOrWhiteSpace(request.SourceRoomId) || string.IsNullOrWhiteSpace(request.DestinationWorldId))
            return BadRequest(new { error = "sourceRoomId and destinationWorldId are required." });

        var destWorld = await _worldRepository.GetWorldAsync(request.DestinationWorldId, ct);
        if (destWorld is null)
            return BadRequest(new { error = $"Destination world '{request.DestinationWorldId}' not found." });

        var portal = new WorldPortal
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceWorldId = worldId,
            SourceRoomId = request.SourceRoomId.Trim(),
            DestinationWorldId = request.DestinationWorldId.Trim(),
            DestinationRoomId = request.DestinationRoomId?.Trim(),
            Description = request.Description?.Trim(),
            NarratorHint = request.NarratorHint?.Trim(),
            IsAdminOnly = request.IsAdminOnly,
            MinLevel = request.MinLevel,
            RequiredCompletedQuests = request.RequiredCompletedQuests ?? []
        };

        world.Portals.Add(portal);
        world.UpdatedAt = DateTimeOffset.UtcNow;
        await _worldRepository.SaveWorldAsync(world, ct);

        return Ok(portal);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPut("admin/worlds/{worldId}/portals/{portalId}")]
    public async Task<IActionResult> UpdatePortal(string worldId, string portalId, [FromBody] CreatePortalRequest request, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        var portal = world.Portals.FirstOrDefault(p => string.Equals(p.Id, portalId, StringComparison.OrdinalIgnoreCase));
        if (portal is null) return NotFound(new { error = $"Portal '{portalId}' not found in world '{worldId}'." });

        if (!string.IsNullOrWhiteSpace(request.SourceRoomId)) portal.SourceRoomId = request.SourceRoomId.Trim();
        if (!string.IsNullOrWhiteSpace(request.DestinationWorldId)) portal.DestinationWorldId = request.DestinationWorldId.Trim();
        if (request.DestinationRoomId is not null) portal.DestinationRoomId = request.DestinationRoomId.Trim();
        if (request.Description is not null) portal.Description = request.Description.Trim();
        if (request.NarratorHint is not null) portal.NarratorHint = request.NarratorHint.Trim();
        portal.IsAdminOnly = request.IsAdminOnly;
        if (request.MinLevel.HasValue) portal.MinLevel = request.MinLevel;
        if (request.RequiredCompletedQuests is not null) portal.RequiredCompletedQuests = request.RequiredCompletedQuests;

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(portal);
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpDelete("admin/worlds/{worldId}/portals/{portalId}")]
    public async Task<IActionResult> DeletePortal(string worldId, string portalId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null) return NotFound(new { error = $"World '{worldId}' not found." });

        var removed = world.Portals.RemoveAll(p => string.Equals(p.Id, portalId, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return NotFound(new { error = $"Portal '{portalId}' not found in world '{worldId}'." });

        world.UpdatedAt = DateTimeOffset.UtcNow;
        await _worldRepository.SaveWorldAsync(world, ct);
        return Ok(new { success = true, id = portalId });
    }

    // ─── World Import / Export ───

    /// <summary>
    /// Exports a world definition (metadata, rules, rooms, NPCs) as a YAML file download.
    /// </summary>
    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/worlds/{worldId}/export")]
    public async Task ExportWorldYaml(string worldId, CancellationToken ct)
    {
        var world = await _worldRepository.GetWorldAsync(worldId, ct);
        if (world is null)
        {
            Response.StatusCode = 404;
            return;
        }

        // Gather rooms belonging to this world
        var allRooms = await _stateManager.GetAllRoomsAsync(ct);
        var worldRooms = allRooms
            .Where(r => r.WorldIds.Contains(worldId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var exportDto = new WorldExportDto
        {
            World = new WorldExportMetadata
            {
                Id = world.Id,
                Name = world.Name,
                Description = world.Description,
                SpawnRoomId = world.SpawnRoomId,
                IsActive = world.IsActive,
                Tags = world.Tags.Count > 0 ? world.Tags : null
            },
            Rules = world.Rules,
            Portals = world.Portals.Count > 0 ? world.Portals.Select(p => new PortalExportDto
            {
                SourceRoomId = p.SourceRoomId,
                DestinationWorldId = p.DestinationWorldId,
                DestinationRoomId = p.DestinationRoomId,
                Description = p.Description,
                NarratorHint = p.NarratorHint,
                IsAdminOnly = p.IsAdminOnly ? true : null,
                MinLevel = p.MinLevel,
                RequiredCompletedQuests = p.RequiredCompletedQuests.Count > 0 ? p.RequiredCompletedQuests : null
            }).ToList() : null,
            Rooms = worldRooms.Count > 0 ? worldRooms.Select(r => new RoomExportDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                Exits = r.Exits.Count > 0 ? r.Exits : null,
                EnvironmentTags = r.EnvironmentTags.Count > 0 ? r.EnvironmentTags : null,
                Npcs = r.Npcs.Count > 0 ? r.Npcs.Select(n => new NpcExportDto
                {
                    Id = n.Id,
                    Name = n.Name,
                    Personality = !string.IsNullOrEmpty(n.Personality) ? n.Personality : null,
                    Faction = n.Faction != "neutral" ? n.Faction : null,
                    KnowledgeScopes = n.KnowledgeScopes.Count > 0 ? n.KnowledgeScopes : null,
                    IsHostile = n.IsHostile ? true : null,
                    IsShopkeeper = n.IsShopkeeper ? true : null,
                    Level = n.Level > 1 ? n.Level : null,
                    Hp = n.Hp,
                    MaxHp = n.MaxHp,
                    AttackBonus = n.AttackBonus,
                    DamageDice = n.DamageDice,
                    Defense = n.Defense,
                    Loot = n.LootTable.Count > 0 ? n.LootTable.Select(ToItemExportDto).ToList() : null,
                    ShopInventory = n.ShopInventory.Count > 0 ? n.ShopInventory.Select(ToItemExportDto).ToList() : null
                }).ToList() : null,
                Items = r.Items.Count > 0 ? r.Items.Select(ToItemExportDto).ToList() : null
            }).ToList() : null
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var yaml = serializer.Serialize(exportDto);

        var safeWorldId = string.Concat(worldId.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-'));
        var fileName = $"world-{safeWorldId}.yaml";
        Response.ContentType = "application/x-yaml";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        await Response.WriteAsync(yaml, ct);
    }

    /// <summary>
    /// Imports a world definition from an uploaded YAML file, creating or updating the world and its rooms.
    /// </summary>
    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/worlds/import")]
    [RequestSizeLimit(5 * 1024 * 1024)] // 5 MB cap
    public async Task<IActionResult> ImportWorldYaml(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A YAML file is required." });

        string yamlContent;
        using (var reader = new StreamReader(file.OpenReadStream()))
            yamlContent = await reader.ReadToEndAsync(ct);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        WorldExportDto dto;
        try
        {
            dto = deserializer.Deserialize<WorldExportDto>(yamlContent);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Invalid YAML: {ex.Message}" });
        }

        if (dto?.World is null || string.IsNullOrWhiteSpace(dto.World.Name))
            return BadRequest(new { error = "YAML must contain a 'world' section with at least a 'name'." });

        // Resolve world ID
        var worldId = dto.World.Id?.Trim();
        if (string.IsNullOrWhiteSpace(worldId))
            worldId = dto.World.Name.Trim().ToLowerInvariant().Replace(' ', '_').Replace("'", "");

        var existing = await _worldRepository.GetWorldAsync(worldId, ct);
        var world = existing ?? new World { Id = worldId, CreatedBy = User.Identity?.Name ?? "admin", CreatedAt = DateTimeOffset.UtcNow };

        world.Name = dto.World.Name.Trim();
        if (dto.World.Description is not null) world.Description = dto.World.Description.Trim();
        if (dto.World.SpawnRoomId is not null) world.SpawnRoomId = dto.World.SpawnRoomId.Trim();
        if (dto.World.IsActive.HasValue) world.IsActive = dto.World.IsActive.Value;
        if (dto.World.Tags is not null) world.Tags = dto.World.Tags;
        if (dto.Rules is not null) world.Rules = dto.Rules;
        world.UpdatedAt = DateTimeOffset.UtcNow;

        // Import portals
        if (dto.Portals is not null)
        {
            world.Portals.Clear();
            foreach (var p in dto.Portals)
            {
                world.Portals.Add(new WorldPortal
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceWorldId = worldId,
                    SourceRoomId = p.SourceRoomId ?? string.Empty,
                    DestinationWorldId = p.DestinationWorldId ?? string.Empty,
                    DestinationRoomId = p.DestinationRoomId,
                    Description = p.Description,
                    NarratorHint = p.NarratorHint,
                    IsAdminOnly = p.IsAdminOnly ?? false,
                    MinLevel = p.MinLevel,
                    RequiredCompletedQuests = p.RequiredCompletedQuests ?? []
                });
            }
        }

        await _worldRepository.SaveWorldAsync(world, ct);

        // Import rooms
        var roomsImported = 0;
        if (dto.Rooms is not null)
        {
            foreach (var roomDto in dto.Rooms)
            {
                var room = ConvertExportRoom(roomDto, worldId);
                await _stateManager.SaveRoomAsync(room, ct);
                roomsImported++;
            }
        }

        return Ok(new
        {
            summary = existing is not null
                ? $"Updated world '{world.Name}' with {roomsImported} room(s)."
                : $"Created world '{world.Name}' with {roomsImported} room(s).",
            worldId = world.Id,
            worldName = world.Name,
            roomsImported,
            isUpdate = existing is not null
        });
    }

    private static ItemExportDto ToItemExportDto(InventoryItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Description = !string.IsNullOrEmpty(item.Description) ? item.Description : null,
        Type = item.Type.ToString().ToLowerInvariant(),
        Quantity = item.Quantity > 1 ? item.Quantity : null,
        Value = item.Value > 0 ? item.Value : null,
        DamageDice = item.DamageDice,
        DamageStat = item.DamageStat,
        ArmorValue = item.ArmorValue > 0 ? item.ArmorValue : null,
        IsEquippable = item.IsEquippable ? true : null,
        IsConsumable = item.IsConsumable ? true : null,
        IsTwoHanded = item.IsTwoHanded ? true : null,
        Effect = item.Effect,
        StatBonuses = item.StatBonuses?.Count > 0 ? item.StatBonuses : null
    };

    private static Room ConvertExportRoom(RoomExportDto dto, string worldId)
    {
        var room = new Room
        {
            Id = dto.Id ?? Guid.NewGuid().ToString("N"),
            Name = dto.Name ?? "Unknown Room",
            Description = dto.Description ?? "An unremarkable room.",
            IsDiscovered = true,
            DiscoveredAt = DateTimeOffset.UtcNow,
            WorldIds = [worldId]
        };

        if (dto.Exits is not null)
            foreach (var exit in dto.Exits)
                room.Exits[exit.Key] = exit.Value;

        if (dto.Npcs is not null)
            room.Npcs.AddRange(dto.Npcs.Select(n => ConvertExportNpc(n, worldId)));

        if (dto.Items is not null)
            room.Items.AddRange(dto.Items.Select(ConvertExportItem));

        if (dto.EnvironmentTags is not null)
            room.EnvironmentTags.AddRange(dto.EnvironmentTags);

        return room;
    }

    private static Npc ConvertExportNpc(NpcExportDto n, string worldId)
    {
        var npc = new Npc
        {
            Id = n.Id ?? Guid.NewGuid().ToString("N"),
            Name = n.Name ?? "Unknown",
            Personality = n.Personality ?? "",
            Faction = n.Faction ?? "neutral",
            IsHostile = n.IsHostile ?? false,
            IsShopkeeper = n.IsShopkeeper ?? false,
            Level = n.Level ?? 1,
            Hp = n.Hp,
            MaxHp = n.MaxHp,
            AttackBonus = n.AttackBonus,
            DamageDice = n.DamageDice,
            Defense = n.Defense,
            WorldIds = [worldId]
        };

        if (n.KnowledgeScopes is not null)
            npc.KnowledgeScopes.AddRange(n.KnowledgeScopes);

        if (n.Loot is not null)
            npc.LootTable.AddRange(n.Loot.Select(ConvertExportItem));

        if (n.ShopInventory is not null)
            npc.ShopInventory.AddRange(n.ShopInventory.Select(ConvertExportItem));

        return npc;
    }

    private static InventoryItem ConvertExportItem(ItemExportDto li)
    {
        var itemType = Enum.TryParse<ItemType>(li.Type, true, out var parsed)
            ? parsed
            : ItemType.Misc;

        return new InventoryItem
        {
            Id = li.Id ?? Guid.NewGuid().ToString("N"),
            Name = li.Name ?? "Unknown Item",
            Description = li.Description ?? "",
            Type = itemType,
            Quantity = Math.Max(1, li.Quantity ?? 1),
            Value = Math.Max(0, li.Value ?? 0),
            DamageDice = li.DamageDice,
            DamageStat = li.DamageStat,
            ArmorValue = Math.Max(0, li.ArmorValue ?? 0),
            IsEquippable = li.IsEquippable ?? InventoryItem.IsEquippableType(itemType),
            IsConsumable = li.IsConsumable ?? itemType is ItemType.Potion or ItemType.Scroll,
            IsTwoHanded = li.IsTwoHanded ?? false,
            Effect = li.Effect,
            StatBonuses = li.StatBonuses ?? new()
        };
    }
}

// ── World Export/Import DTOs ─────────────────────────────
public class WorldExportDto
{
    public WorldExportMetadata? World { get; set; }
    public GameRulesConfig? Rules { get; set; }
    public List<PortalExportDto>? Portals { get; set; }
    public List<RoomExportDto>? Rooms { get; set; }
}

public class WorldExportMetadata
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SpawnRoomId { get; set; }
    public bool? IsActive { get; set; }
    public List<string>? Tags { get; set; }
}

public class PortalExportDto
{
    public string? SourceRoomId { get; set; }
    public string? DestinationWorldId { get; set; }
    public string? DestinationRoomId { get; set; }
    public string? Description { get; set; }
    public string? NarratorHint { get; set; }
    public bool? IsAdminOnly { get; set; }
    public int? MinLevel { get; set; }
    public List<string>? RequiredCompletedQuests { get; set; }
}

public class RoomExportDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Exits { get; set; }
    public List<NpcExportDto>? Npcs { get; set; }
    public List<ItemExportDto>? Items { get; set; }
    public List<string>? EnvironmentTags { get; set; }
}

public class NpcExportDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public string? Faction { get; set; }
    public List<string>? KnowledgeScopes { get; set; }
    public bool? IsHostile { get; set; }
    public bool? IsShopkeeper { get; set; }
    public int? Level { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? AttackBonus { get; set; }
    public string? DamageDice { get; set; }
    public int? Defense { get; set; }
    public List<ItemExportDto>? Loot { get; set; }
    public List<ItemExportDto>? ShopInventory { get; set; }
}

public class ItemExportDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public int? Quantity { get; set; }
    public int? Value { get; set; }
    public string? DamageDice { get; set; }
    public string? DamageStat { get; set; }
    public int? ArmorValue { get; set; }
    public bool? IsEquippable { get; set; }
    public bool? IsConsumable { get; set; }
    public bool? IsTwoHanded { get; set; }
    public string? Effect { get; set; }
    public Dictionary<string, int>? StatBonuses { get; set; }
}

public class CreateWorldRequest
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? SpawnRoomId { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string>? Tags { get; set; }
    public GameRulesConfig? Rules { get; set; }
}

public class UpdateWorldRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? SpawnRoomId { get; set; }
    public bool? IsActive { get; set; }
    public List<string>? Tags { get; set; }
    public GameRulesConfig? Rules { get; set; }
    public string? CharacterCreationIntro { get; set; }
    public string? DefaultNarratorPresetId { get; set; }
    public List<string>? NarratorPresetIds { get; set; }
}

public class CreatePortalRequest
{
    public string SourceRoomId { get; set; } = string.Empty;
    public string DestinationWorldId { get; set; } = string.Empty;
    public string? DestinationRoomId { get; set; }
    public string? Description { get; set; }
    public string? NarratorHint { get; set; }
    public bool IsAdminOnly { get; set; }
    public int? MinLevel { get; set; }
    public List<string>? RequiredCompletedQuests { get; set; }
}

public class ContentGenerateRequest
{
    public string ContentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ExistingJson { get; set; }
}

public class GenerateQuestRequest
{
    /// <summary>1-3 sentences of direction for the quest.</summary>
    public string Brief { get; set; } = string.Empty;
    /// <summary>World ID to pull NPC/room context from.</summary>
    public string? WorldId { get; set; }
    /// <summary>Lore entry IDs to include as context for the AI.</summary>
    public List<string>? LoreEntryIds { get; set; }
    /// <summary>Minimum player level for the quest.</summary>
    public int? MinLevel { get; set; }
    /// <summary>Maximum player level for the quest.</summary>
    public int? MaxLevel { get; set; }
}

public class ActionRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}

public class CreateCharacterRequest
{
    public string? PlayerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string? Backstory { get; set; }
    public string StatMethod { get; set; } = "StandardArray";
}

public class SeedDemoCharactersRequest
{
    public bool ReplaceExisting { get; set; }
}

public class ResetWorldRequest
{
    /// <summary>
    /// If true, keeps existing players but resets them to spawn with full HP/MP and empty inventory.
    /// If false, deletes all players too.
    /// </summary>
    public bool KeepPlayers { get; set; } = true;
}

public class SendMessageRequest
{
    /// <summary>Player ID to send to. If null/empty, broadcasts to ALL players.</summary>
    public string? PlayerId { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class EditPlayerRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Race { get; set; }
    public string? Class { get; set; }
    public string? Backstory { get; set; }
    public string? CurrentRoomId { get; set; }
    public string? DiscordId { get; set; }
    public ulong? ThreadId { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? Mp { get; set; }
    public int? MaxMp { get; set; }
    public int? Gold { get; set; }
    public int? Xp { get; set; }
    public int? Level { get; set; }
    public int? Str { get; set; }
    public int? Dex { get; set; }
    public int? Con { get; set; }
    public int? Int { get; set; }
    public int? Wis { get; set; }
    public int? Cha { get; set; }
    public int? Luck { get; set; }
}

public class AdjustResourcesRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public int HpDelta { get; set; }
    public int MpDelta { get; set; }
    public int GoldDelta { get; set; }
    public int XpDelta { get; set; }
    public int LevelDelta { get; set; }
    public int? SetHp { get; set; }
    public int? SetMaxHp { get; set; }
    public int? SetMp { get; set; }
    public int? SetMaxMp { get; set; }
    public int? SetGold { get; set; }
    public int? SetXp { get; set; }
    public int? SetLevel { get; set; }
}

public class TeleportPlayerRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string? RoomName { get; set; }
    public string? RoomDescription { get; set; }
    public bool CreateRoomIfMissing { get; set; } = true;
    public bool ConnectFromCurrentRoom { get; set; } = true;
    public string? EntryDirection { get; set; } = "north";
    public string[]? EnvironmentTags { get; set; }
}

public class GrantItemRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string? ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = nameof(ItemType.Misc);
    public int Quantity { get; set; } = 1;
    public int Value { get; set; }
    public string? Description { get; set; }
    public string? DamageDice { get; set; }
    public string? DamageStat { get; set; }
    public int ArmorValue { get; set; }
    public bool? IsEquippable { get; set; }
    public bool? IsConsumable { get; set; }
    public bool IsTwoHanded { get; set; }
    public string? Effect { get; set; }
    public Dictionary<string, int>? StatBonuses { get; set; }
    public bool AutoEquip { get; set; }
}

public class PlayerItemActionRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string Action { get; set; } = "remove";
}

public class ApplyStatusRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string? StatusId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = nameof(StatusEffectType.Buff);
    public string? Description { get; set; }
    public int RemainingTurns { get; set; } = 3;
    public int DamagePerTurn { get; set; }
    public int HealPerTurn { get; set; }
    public bool ReplaceExisting { get; set; } = true;
    public Dictionary<string, int>? StatModifiers { get; set; }
    public string? StatModifiersText { get; set; }
}

public class UpsertRoomFixtureRequest
{
    public string RoomId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? IsDiscovered { get; set; }
    public bool ClearItems { get; set; }
    public bool ClearNpcs { get; set; }
    public bool ClearTags { get; set; }
    public string[]? EnvironmentTags { get; set; }
    public Dictionary<string, string>? Exits { get; set; }
    public List<RoomFixtureItemRequest>? Items { get; set; }
    public List<RoomFixtureNpcRequest>? Npcs { get; set; }
}

public class RoomFixtureItemRequest
{
    public string? ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = nameof(ItemType.Misc);
    public string? Description { get; set; }
    public int Quantity { get; set; } = 1;
    public int Value { get; set; }
    public bool? IsEquippable { get; set; }
    public bool? IsConsumable { get; set; }
}

public class RoomFixtureNpcRequest
{
    public string? NpcId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Personality { get; set; }
    public string? Faction { get; set; }
    public bool IsHostile { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? AttackBonus { get; set; }
    public string? DamageDice { get; set; }
    public int? Defense { get; set; }
    public int Level { get; set; } = 1;
    public List<string>? KnowledgeScopes { get; set; }
}
