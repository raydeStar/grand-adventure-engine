using GAE.Core.Interfaces;
using GAE.Core.Models;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public DashboardController(
        IStateManager stateManager,
        IGameEngine engine,
        IGameEventBroadcaster broadcaster,
        IWikiService wikiService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _stateManager = stateManager;
        _engine = engine;
        _broadcaster = broadcaster;
        _wikiService = wikiService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
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

        var narratorEndpoint = (_configuration["LmStudio:Endpoint"] ?? "http://localhost:1234").TrimEnd('/');

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(5);
            using var response = await httpClient.GetAsync($"{narratorEndpoint}/v1/models", ct);
            checks["health/narrator"] = response.IsSuccessStatusCode
                ? new { ok = true, status = "healthy", service = "lm-studio" }
                : new { ok = false, status = "degraded", service = "lm-studio", note = "Narration will use fallback text" };
        }
        catch (Exception ex)
        {
            checks["health/narrator"] = new { ok = false, status = "degraded", service = "lm-studio", error = ex.Message, note = "Narration will use fallback text" };
        }

        return Ok(checks);
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
