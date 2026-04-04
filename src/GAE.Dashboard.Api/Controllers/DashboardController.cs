using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GAE.Dashboard.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = DashboardPolicies.UserAccess)]
public class DashboardController : ControllerBase
{
    private readonly IStateManager _stateManager;
    private readonly IGameEngine _engine;
    private readonly IGameEventBroadcaster _broadcaster;
    private readonly IWikiService _wikiService;
    private readonly INarratorService _narrator;
    private readonly IConversationLogger _conversationLogger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly WorldSeedConfig _worldSeedConfig;
    private readonly IContentRegistryService _registry;

    private static readonly TimeSpan NarratorCacheDuration = TimeSpan.FromSeconds(60);
    private static object? _narratorCachedResult;
    private static DateTimeOffset _narratorCacheExpiry;

    public DashboardController(
        IStateManager stateManager,
        IGameEngine engine,
        IGameEventBroadcaster broadcaster,
        IWikiService wikiService,
        INarratorService narrator,
        IConversationLogger conversationLogger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        WorldSeedConfig worldSeedConfig,
        IContentRegistryService registry)
    {
        _stateManager = stateManager;
        _engine = engine;
        _broadcaster = broadcaster;
        _wikiService = wikiService;
        _narrator = narrator;
        _conversationLogger = conversationLogger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _worldSeedConfig = worldSeedConfig;
        _registry = registry;
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
    public async Task<IActionResult> GetRooms(CancellationToken ct)
    {
        var rooms = await _stateManager.GetAllRoomsAsync(ct);
        return Ok(rooms);
    }

    [HttpGet("rooms/{roomId}")]
    public async Task<IActionResult> GetRoom(string roomId, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(roomId, ct);
        return room is not null ? Ok(room) : NotFound();
    }

    [HttpGet("story")]
    public async Task<IActionResult> GetStory([FromQuery] string? playerId = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var entries = await _stateManager.GetStoryEntriesAsync(playerId, limit, ct);
        return Ok(entries);
    }

    [HttpGet("story/room/{roomId}")]
    public async Task<IActionResult> GetRoomStory(string roomId, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        var entries = await _stateManager.GetRecentStoryForRoomAsync(roomId, limit, ct);
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

        try
        {
            var healthy = await _wikiService.IsHealthyAsync(ct);
            checks["health/wiki"] = healthy
                ? new { ok = true, status = "healthy", service = "wiki.js" }
                : new { ok = false, status = "degraded", service = "wiki.js", note = "Wiki sync unavailable — game continues without wiki" };
        }
        catch (Exception ex)
        {
            checks["health/wiki"] = new { ok = false, status = "degraded", service = "wiki.js", error = ex.Message, note = "Wiki sync unavailable — game continues without wiki" };
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

    public class SetModelRequest
    {
        public string Model { get; set; } = string.Empty;
    }

    [HttpPost("action")]
    public async Task<IActionResult> ProcessAction([FromBody] ActionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlayerId) || string.IsNullOrWhiteSpace(request.Command))
            return BadRequest(new { error = "playerId and command are required." });

        var action = _engine.ParseCommand(request.PlayerId, request.Command);
        var result = await _engine.ProcessActionAsync(request.PlayerId, action, ct);
        return Ok(result);
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
        var activeThreshold = DateTimeOffset.UtcNow.AddMinutes(-30);

        return Ok(new
        {
            playerCount = players.Count,
            activePlayerCount = players.Count(player => player.LastActiveAt >= activeThreshold),
            roomCount = rooms.Count,
            discoveredRoomCount = rooms.Count(room => room.IsDiscovered),
            storyEntryCount = storyEntries.Count,
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
                player.CurrentRoomId = "spawn";
                player.Hp = player.MaxHp;
                player.Mp = player.MaxMp;
                player.Inventory.Clear();
                player.Equipment = new GAE.Core.Models.EquipmentLoadout();
                player.StatusEffects.Clear();
                player.Interaction = new GAE.Core.Models.InteractionState();
                player.Gold = 50;
                player.Xp = 0;
                player.Level = 1;
                player.LastActiveAt = DateTimeOffset.UtcNow;
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
            IsEquippable = li.IsEquippable ?? itemType is ItemType.Weapon or ItemType.Armor or ItemType.Shield or ItemType.Helmet,
            IsConsumable = li.IsConsumable ?? itemType is ItemType.Potion or ItemType.Scroll,
            Effect = li.Effect
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

        if (request.CurrentRoomId is not null) { player.CurrentRoomId = request.CurrentRoomId.Trim(); changes.Add("room"); }

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
            IsEquippable = request.IsEquippable ?? itemType is ItemType.Weapon or ItemType.Armor or ItemType.Shield or ItemType.Helmet,
            Effect = string.IsNullOrWhiteSpace(request.Effect) ? null : request.Effect.Trim()
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
                IsHostile = npcRequest.IsHostile
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
            IsEquippable = request.IsEquippable ?? itemType is ItemType.Weapon or ItemType.Armor or ItemType.Shield or ItemType.Helmet
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
        switch (item.Type)
        {
            case ItemType.Weapon:
                if (player.Equipment.Weapon is not null)
                    player.Inventory.Add(player.Equipment.Weapon);
                player.Equipment.Weapon = item;
                return "Weapon";
            case ItemType.Armor:
                if (player.Equipment.Armor is not null)
                    player.Inventory.Add(player.Equipment.Armor);
                player.Equipment.Armor = item;
                return "Armor";
            case ItemType.Shield:
                if (player.Equipment.Shield is not null)
                    player.Inventory.Add(player.Equipment.Shield);
                player.Equipment.Shield = item;
                return "Shield";
            case ItemType.Helmet:
                if (player.Equipment.Helmet is not null)
                    player.Inventory.Add(player.Equipment.Helmet);
                player.Equipment.Helmet = item;
                return "Helmet";
            default:
                return null;
        }
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
    public async Task<IActionResult> DmSearch([FromQuery] string q, [FromQuery] string? type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { results = Array.Empty<object>() });

        var query = q.Trim().ToLowerInvariant();
        var filter = type?.Trim().ToLowerInvariant();
        var results = new List<object>();

        // Search spells
        if (filter is null or "spell")
            foreach (var s in _registry.Spells.GetAll())
                if (Matches(s.Id, s.Name, s.Description, s.School, s.Tags, query))
                    results.Add(new { type = "spell", s.Id, s.Name, s.Description, meta = $"{s.School} | Power {s.PowerLevel} | {s.ManaCost} MP", data = s });

        // Search items
        if (filter is null or "item")
            foreach (var i in _registry.Items.GetAll())
                if (Matches(i.Id, i.Name, i.Description, i.Rarity, i.Tags, query))
                    results.Add(new { type = "item", i.Id, i.Name, i.Description, meta = $"{i.Type} | {i.Rarity} | {i.Value}g", data = i });

        // Search classes
        if (filter is null or "class")
            foreach (var c in _registry.Classes.GetAll())
                if (Matches(c.Id, c.Name, c.Description, null, c.Tags, query))
                    results.Add(new { type = "class", c.Id, c.Name, c.Description, meta = $"{c.HitDie} | {c.PrimaryStat}{(c.CanCastSpells ? " | Caster" : "")}", data = c });

        // Search races
        if (filter is null or "race")
            foreach (var r in _registry.Races.GetAll())
                if (Matches(r.Id, r.Name, r.Description, null, r.Tags, query))
                    results.Add(new { type = "race", r.Id, r.Name, r.Description, meta = string.Join(", ", r.Traits), data = r });

        // Fetch rooms once if needed for room or NPC search
        var needRooms = filter is null or "room" or "npc";
        var rooms = needRooms ? await _stateManager.GetAllRoomsAsync(ct) : [];

        // Search rooms
        if (filter is null or "room")
            foreach (var rm in rooms)
                if (rm.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    rm.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (rm.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    results.Add(new { type = "room", id = rm.Id, name = rm.Name, description = rm.Description,
                        meta = $"{rm.Npcs.Count} NPCs | {rm.Items.Count} items | {rm.Exits.Count} exits", data = rm });

        // Search players
        if (filter is null or "player")
        {
            var players = await _stateManager.GetAllPlayersAsync(ct);
            foreach (var p in players)
                if (p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Race.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Class.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(new { type = "player", id = p.Id, name = p.Name,
                        description = $"Lv.{p.Level} {p.Race} {p.Class}",
                        meta = $"HP: {p.Hp}/{p.MaxHp} | MP: {p.Mp}/{p.MaxMp} | Gold: {p.Gold}", data = p });
        }

        // Search NPCs (across all rooms)
        if (filter is null or "npc")
            foreach (var rm in rooms)
                foreach (var npc in rm.Npcs)
                    if (npc.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        npc.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (npc.Personality?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        npc.Faction.Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add(new { type = "npc", id = npc.Id, name = npc.Name,
                            description = npc.Personality,
                            meta = $"{npc.Faction} | Lv.{npc.Level}{(npc.IsHostile ? " | Hostile" : "")} | Room: {rm.Name}", data = npc, roomId = rm.Id });

        return Ok(new { results, total = results.Count });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpGet("admin/dm/browse/{type}")]
    public async Task<IActionResult> DmBrowse(string type, CancellationToken ct)
    {
        var results = new List<object>();
        switch (type.ToLowerInvariant())
        {
            case "spells":
                foreach (var s in _registry.Spells.GetAll())
                    results.Add(new { type = "spell", s.Id, s.Name, s.Description, meta = $"{s.School} | Power {s.PowerLevel} | {s.ManaCost} MP", data = s });
                break;
            case "items":
                foreach (var i in _registry.Items.GetAll())
                    results.Add(new { type = "item", i.Id, i.Name, i.Description, meta = $"{i.Type} | {i.Rarity} | {i.Value}g", data = i });
                break;
            case "classes":
                foreach (var c in _registry.Classes.GetAll())
                    results.Add(new { type = "class", c.Id, c.Name, c.Description, meta = $"{c.HitDie} | {c.PrimaryStat}{(c.CanCastSpells ? " | Caster" : "")}", data = c });
                break;
            case "races":
                foreach (var r in _registry.Races.GetAll())
                    results.Add(new { type = "race", r.Id, r.Name, r.Description, meta = string.Join(", ", r.Traits), data = r });
                break;
            case "rooms":
                foreach (var rm in await _stateManager.GetAllRoomsAsync(ct))
                    results.Add(new { type = "room", id = rm.Id, name = rm.Name, description = rm.Description,
                        meta = $"{rm.Npcs.Count} NPCs | {rm.Items.Count} items | {rm.Exits.Count} exits", data = rm });
                break;
            case "players":
                foreach (var p in await _stateManager.GetAllPlayersAsync(ct))
                    results.Add(new { type = "player", id = p.Id, name = p.Name,
                        description = $"Lv.{p.Level} {p.Race} {p.Class}",
                        meta = $"HP: {p.Hp}/{p.MaxHp} | MP: {p.Mp}/{p.MaxMp} | Gold: {p.Gold}", data = p });
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
            spellList = _registry.Spells.GetAll().Select(s => new { s.Id, s.Name, s.School, s.PowerLevel, s.ManaCost, s.RequiredLevel }),
            classList = _registry.Classes.GetAll().Select(c => new { c.Id, c.Name, c.CanCastSpells, c.PrimaryStat }),
            raceList = _registry.Races.GetAll().Select(r => new { r.Id, r.Name, r.Traits }),
        });
    }

    [Authorize(Policy = DashboardPolicies.AdminAccess)]
    [HttpPost("admin/registry/{type}")]
    public IActionResult UpsertRegistryEntry(string type, [FromBody] System.Text.Json.JsonElement body)
    {
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };

            switch (type.ToLowerInvariant())
            {
                case "spells":
                    var spell = System.Text.Json.JsonSerializer.Deserialize<SpellDefinition>(body.GetRawText(), options);
                    if (spell is null || string.IsNullOrWhiteSpace(spell.Id)) return BadRequest(new { error = "Invalid spell data" });
                    _registry.Spells.Register(spell);
                    return Ok(spell);
                case "classes":
                    var cls = System.Text.Json.JsonSerializer.Deserialize<ClassDefinition>(body.GetRawText(), options);
                    if (cls is null || string.IsNullOrWhiteSpace(cls.Id)) return BadRequest(new { error = "Invalid class data" });
                    _registry.Classes.Register(cls);
                    return Ok(cls);
                case "races":
                    var race = System.Text.Json.JsonSerializer.Deserialize<RaceDefinition>(body.GetRawText(), options);
                    if (race is null || string.IsNullOrWhiteSpace(race.Id)) return BadRequest(new { error = "Invalid race data" });
                    _registry.Races.Register(race);
                    return Ok(race);
                case "items":
                    var item = System.Text.Json.JsonSerializer.Deserialize<ItemTemplate>(body.GetRawText(), options);
                    if (item is null || string.IsNullOrWhiteSpace(item.Id)) return BadRequest(new { error = "Invalid item data" });
                    _registry.Items.Register(item);
                    return Ok(item);
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
    public IActionResult DeleteRegistryEntry(string type, string id)
    {
        switch (type.ToLowerInvariant())
        {
            case "spells": _registry.Spells.Remove(id); break;
            case "classes": _registry.Classes.Remove(id); break;
            case "races": _registry.Races.Remove(id); break;
            case "items": _registry.Items.Remove(id); break;
            default: return NotFound(new { error = $"Unknown registry type: {type}" });
        }
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
}

public class ContentGenerateRequest
{
    public string ContentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ExistingJson { get; set; }
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

public class EditPlayerRequest
{
    public string PlayerId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Race { get; set; }
    public string? Class { get; set; }
    public string? Backstory { get; set; }
    public string? CurrentRoomId { get; set; }
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
    public string? Effect { get; set; }
    public bool AutoEquip { get; set; }
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
}
