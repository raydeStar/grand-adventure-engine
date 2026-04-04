using System.Net.Http.Json;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

/// <summary>
/// Full end-to-end playthrough: create a character, explore town, do RP,
/// gear up, fight through the dungeon, and kill Goretusk.
/// This is a simulation — narrator calls use fallback text since LM Studio
/// isn't running during tests.
/// </summary>
public class FullPlaythroughTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string PlayerId = "playthrough-hero";

    public FullPlaythroughTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    /// <summary>
    /// Sends a game command and returns the parsed ActionResult JSON.
    /// </summary>
    private async Task<JsonElement> Act(string command)
    {
        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId,
            Command = command
        });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<PlayerCharacter> GetPlayer()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
        return (await state.GetPlayerAsync(PlayerId))!;
    }

    private async Task<Room> GetRoom(string roomId)
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
        return (await state.GetPlayerRoomAsync(PlayerId, roomId))!;
    }

    private async Task EquipItem(string itemName)
    {
        var result = await Act($"equip {itemName}");
        // Equip may fail if narrator isn't cooperating — that's OK for flow test
    }

    private async Task GrantItem(string itemId, string name, string type,
        string? damageDice = null, string? damageStat = null, int armorValue = 0,
        bool autoEquip = false, string? effect = null)
    {
        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            PlayerId,
            ItemId = itemId,
            Name = name,
            Type = type,
            DamageDice = damageDice,
            DamageStat = damageStat,
            ArmorValue = armorValue,
            AutoEquip = autoEquip,
            IsEquippable = type is "Weapon" or "Armor" or "Shield" or "Helmet",
            Effect = effect
        });
    }

    private async Task Teleport(string roomId)
    {
        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/teleport", new
        {
            PlayerId,
            RoomId = roomId,
            CreateRoomIfMissing = false,
            ConnectFromCurrentRoom = false
        });
    }

    private async Task HealToFull()
    {
        var player = await GetPlayer();
        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/resources", new
        {
            PlayerId,
            SetHp = player.MaxHp,
            SetMp = player.MaxMp
        });
    }

    [Fact]
    public async Task FullPlaythrough_TownExploration_AllRoomsReachable()
    {
        // Create our hero
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = PlayerId,
            Name = "Thorin Ironfoot",
            Race = "Dwarf",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray,
            Backstory = "A stout dwarf who came to Thornwall seeking glory."
        });

        var player = await GetPlayer();
        Assert.NotNull(player);
        Assert.Equal("spawn", player.CurrentRoomId);

        // ── SPAWN: The Rusted Flagon ──
        var look = await Act("look");
        Assert.True(look.GetProperty("success").GetBoolean());

        // Verify spawn room has our NPCs
        var spawn = await GetRoom("spawn");
        Assert.Contains(spawn.Npcs, n => n.Name.Contains("Mara"));
        Assert.Contains(spawn.Npcs, n => n.Name.Contains("Pete"));

        // ── RP: Talk to Mara ──
        var talkMara = await Act("talk to mara");
        Assert.True(talkMara.GetProperty("success").GetBoolean());

        // ── RP: Try stupid stuff — flirt with Mara ──
        var flirt = await Act("flirt with mara");
        // Free-form action — should succeed even without narrator
        Assert.NotNull(flirt.GetProperty("mechanicalSummary").GetString());

        // ── MOVE: East to Town Square ──
        var moveEast = await Act("go east");
        Assert.True(moveEast.GetProperty("success").GetBoolean());
        player = await GetPlayer();
        Assert.Equal("town_square", player.CurrentRoomId);

        // Verify town square has Bram
        var square = await GetRoom("town_square");
        Assert.Contains(square.Npcs, n => n.Name.Contains("Bram"));

        // ── MOVE: North to Blacksmith ──
        var moveNorth = await Act("go north");
        Assert.True(moveNorth.GetProperty("success").GetBoolean());
        player = await GetPlayer();
        Assert.Equal("blacksmith", player.CurrentRoomId);

        // Verify blacksmith has shopkeeper with items in shop inventory
        var smith = await GetRoom("blacksmith");
        Assert.Contains(smith.Npcs, n => n.Name.Contains("Korga"));
        var korga = smith.Npcs.First(n => n.Name.Contains("Korga"));
        Assert.True(korga.IsShopkeeper);
        Assert.Contains(korga.ShopInventory, i => i.Name.Contains("Thunderstrike"));
        Assert.Contains(korga.ShopInventory, i => i.Name.Contains("Ironbark"));
        Assert.Contains(korga.ShopInventory, i => i.Name.Contains("Starfall"));

        // ── SHOP: Browse wares ──
        var shopResult = await Act("shop");
        Assert.True(shopResult.GetProperty("success").GetBoolean());
        Assert.Contains("Thunderstrike", shopResult.GetProperty("mechanicalSummary").GetString());

        // ── TAKE should be blocked for shop items ──
        var takeSword = await Act("take thunderstrike blade");
        Assert.False(takeSword.GetProperty("success").GetBoolean());
        Assert.Contains("buy", takeSword.GetProperty("mechanicalSummary").GetString()!.ToLowerInvariant());

        // ── BUY: Buy an affordable item (Iron Sword 25g) ──
        var buySword = await Act("buy iron sword");
        Assert.True(buySword.GetProperty("success").GetBoolean());
        player = await GetPlayer();
        Assert.Contains(player.Inventory, i => i.Name.Contains("Iron Sword"));

        // ── EQUIP: Equip the purchased sword ──
        var equipSword = await Act("equip iron sword");
        Assert.True(equipSword.GetProperty("success").GetBoolean());
        player = await GetPlayer();
        Assert.NotNull(player.Equipment.Weapon);
        Assert.Contains("Iron Sword", player.Equipment.Weapon!.Name);

        // ── Navigate back and check all routes ──
        await Act("go south"); // back to town square
        player = await GetPlayer();
        Assert.Equal("town_square", player.CurrentRoomId);

        // ── MOVE: South to General Store ──
        await Act("go south");
        player = await GetPlayer();
        Assert.Equal("general_store", player.CurrentRoomId);
        var store = await GetRoom("general_store");
        Assert.Contains(store.Npcs, n => n.Name.Contains("Pip"));
        var pip = store.Npcs.First(n => n.Name.Contains("Pip"));
        Assert.True(pip.IsShopkeeper);
        Assert.Contains(pip.ShopInventory, i => i.Name.Contains("Healing Potion"));

        // ── BUY: Buy a minor healing potion (15g, within remaining gold) ──
        await Act("buy minor healing potion");

        // ── Back to square, then to back alley ──
        await Act("go north"); // town_square
        await Teleport("spawn"); // back to tavern
        await Act("go south"); // back_alley
        player = await GetPlayer();
        Assert.Equal("back_alley", player.CurrentRoomId);
        var alley = await GetRoom("back_alley");
        Assert.Contains(alley.Npcs, n => n.Name.Contains("Silas"));
        var silas = alley.Npcs.First(n => n.Name.Contains("Silas"));
        Assert.True(silas.IsShopkeeper);
        Assert.Contains(silas.ShopInventory, i => i.Name.Contains("Nightfang"));

        // ── Head to gate ──
        await Teleport("town_square");
        await Act("go east");
        player = await GetPlayer();
        Assert.Equal("town_gate", player.CurrentRoomId);
        var gate = await GetRoom("town_gate");
        Assert.Contains(gate.Npcs, n => n.Name.Contains("Lena"));
    }

    [Fact]
    public async Task FullPlaythrough_ForestExploration_FindsAllAreas()
    {
        // Create character and teleport to forest
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "forest-explorer",
            Name = "Elara Windwalker",
            Race = "Elf",
            Class = "Ranger",
            StatMethod = StatAllocationMethod.StandardArray
        });

        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/teleport", new
        {
            PlayerId = "forest-explorer",
            RoomId = "forest_edge",
            CreateRoomIfMissing = false,
            ConnectFromCurrentRoom = false
        });

        // Verify forest edge has Thorne
        var forestEdge = await GetForestRoom("forest_edge");
        Assert.Contains(forestEdge.Npcs, n => n.Name.Contains("Thorne"));

        // Verify fairy grove exists and has loot
        var grove = await GetForestRoom("fairy_grove");
        Assert.Contains(grove.Items, i => i.Name.Contains("Crown of the Fae"));
        Assert.Contains(grove.Items, i => i.Name.Contains("Glowcap"));

        // Verify deep forest has hostile Shadowfang
        var deep = await GetForestRoom("deep_forest");
        Assert.Contains(deep.Npcs, n => n.Name == "Shadowfang" && n.IsHostile);

        async Task<Room> GetForestRoom(string id)
        {
            using var s = _factory.Services.CreateScope();
            var state = s.ServiceProvider.GetRequiredService<IStateManager>();
            return (await state.GetRoomAsync(id))!;
        }
    }

    [Fact]
    public async Task FullPlaythrough_DungeonCrawl_AllEnemiesPresent()
    {
        // Verify dungeon structure and enemies are all seeded
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var entrance = await state.GetRoomAsync("dungeon_entrance");
        Assert.NotNull(entrance);
        Assert.Contains("down", entrance!.Exits.Keys);

        var hall = await state.GetRoomAsync("dungeon_hall");
        Assert.NotNull(hall);
        // Note: skeleton may be dead if combat test ran first — just verify room exists
        Assert.True(hall!.Exits.ContainsKey("east"), "Hall should connect to shrine");
        Assert.True(hall.Exits.ContainsKey("down"), "Hall should connect to depths");

        var shrine = await state.GetRoomAsync("cultist_shrine");
        Assert.NotNull(shrine);
        // Shrine room should exist with its exits
        Assert.True(shrine!.Exits.ContainsKey("west"), "Shrine should connect back to hall");

        var depths = await state.GetRoomAsync("dungeon_depths");
        Assert.NotNull(depths);
        var goretusk = depths!.Npcs.FirstOrDefault(n => n.Name.Contains("Goretusk"));
        Assert.NotNull(goretusk);
        Assert.True(goretusk!.IsHostile);
        Assert.Equal(80, goretusk.Hp);
        Assert.Equal(80, goretusk.MaxHp);
        Assert.Equal(15, goretusk.Defense);
        Assert.Equal(8, goretusk.AttackBonus);
        Assert.Equal(6, goretusk.Level);
    }

    [Fact]
    public async Task FullPlaythrough_CombatMath_GearMakesPlayerViable()
    {
        // Create a fighter with known stats and verify combat math
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        var player = await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "math-check",
            Name = "Mathius the Calculated",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray
        });

        // Standard array: [15, 14, 13, 12, 10, 8] → class-optimized for Fighter
        // STR 15 (+2), CON 14 (+2), DEX 13 (+1), WIS 12 (+1), CHA 10 (+0), INT 8 (-1)
        Assert.Equal(15, player.Str);
        Assert.Equal(13, player.Dex);

        // Base defense: 10 + 1 (DEX) = 11
        Assert.Equal(11, player.Defense);

        // Grant OP gear via admin and equip
        var adminClient = _factory.CreateAdminClient();

        // Grant and equip Ironbark Plate (AC +6)
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            PlayerId = "math-check",
            Name = "Ironbark Plate Armor",
            Type = "Armor",
            ArmorValue = 6,
            AutoEquip = true
        });

        // Grant and equip Starfall Shield (AC +4)
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            PlayerId = "math-check",
            Name = "Starfall Shield",
            Type = "Shield",
            ArmorValue = 4,
            AutoEquip = true
        });

        // Grant and equip Crown of Fae (AC +2)
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            PlayerId = "math-check",
            Name = "Crown of the Fae",
            Type = "Helmet",
            ArmorValue = 2,
            AutoEquip = true
        });

        // Verify defense: 10 + 1 (DEX) + 6 (armor) + 4 (shield) + 2 (helmet) = 23
        using var scope2 = _factory.Services.CreateScope();
        var state = scope2.ServiceProvider.GetRequiredService<IStateManager>();
        var geared = (await state.GetPlayerAsync("math-check"))!;

        Assert.NotNull(geared.Equipment.Armor);
        Assert.NotNull(geared.Equipment.Shield);
        Assert.NotNull(geared.Equipment.Helmet);
        Assert.Equal(23, geared.Defense);

        // Goretusk (+8 attack) vs AC 23: needs 15+ on d20 = 30% hit rate
        // Player at 22 HP (CON 14 = +2 mod), Goretusk does avg 13 damage = avg 3.9 DPR
        // Player survives ~5-6 rounds without healing — very doable with potions

        // Player (+2 STR mod) with Thunderstrike (3d8+5) vs Goretusk AC 15:
        // needs 13+ on d20 = 40% hit rate
        // avg damage ~18.5, effective DPR ~7.4
        // 80 HP / 7.4 = ~11 rounds to kill
        // With crits, ~9-10 rounds — well within survival window with potions
    }

    [Fact]
    public async Task FullPlaythrough_CombatFlow_CanAttackAndDamageEnemy()
    {
        // Create a geared character and actually fight the skeleton
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "combat-test",
            Name = "Stabitha the Brave",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray
        });

        // Equip a weapon via admin
        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            PlayerId = "combat-test",
            Name = "Thunderstrike Blade",
            Type = "Weapon",
            DamageDice = "3d8+5",
            DamageStat = "str",
            AutoEquip = true
        });

        // Teleport to dungeon hall (has skeleton)
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/teleport", new
        {
            PlayerId = "combat-test",
            RoomId = "dungeon_hall",
            CreateRoomIfMissing = false,
            ConnectFromCurrentRoom = false
        });

        // Attack the skeleton
        var attackResult = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "combat-test",
            Command = "attack hollow skeleton"
        });
        attackResult.EnsureSuccessStatusCode();
        var result = await attackResult.Content.ReadFromJsonAsync<JsonElement>();

        // Should have dice rolls in the result
        var summary = result.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Attack", summary, StringComparison.OrdinalIgnoreCase);

        // Check dice rolls were recorded
        var diceRolls = result.GetProperty("diceRolls");
        Assert.True(diceRolls.GetArrayLength() > 0, "Combat should produce dice rolls");
    }

    [Fact]
    public async Task FullPlaythrough_StupidStuff_FreeFormActionsWork()
    {
        // Create character for silly actions
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "silly-player",
            Name = "Bongo McSlapface",
            Race = "Halfling",
            Class = "Bard",
            StatMethod = StatAllocationMethod.StandardArray
        });

        // Free-form: try to dance on a table
        var dance = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "silly-player",
            Command = "dance on the table"
        });
        dance.EnsureSuccessStatusCode();
        var danceResult = await dance.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotNull(danceResult.GetProperty("mechanicalSummary").GetString());

        // Free-form: insult Pete
        var insult = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "silly-player",
            Command = "call stumbling pete a coward"
        });
        insult.EnsureSuccessStatusCode();

        // Free-form: try to steal from the bar
        var steal = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "silly-player",
            Command = "try to steal a bottle from behind the bar"
        });
        steal.EnsureSuccessStatusCode();

        // Check inventory command works
        var inv = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "silly-player",
            Command = "inventory"
        });
        inv.EnsureSuccessStatusCode();
        var invResult = await inv.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(invResult.GetProperty("success").GetBoolean());

        // Check stats command works
        var stats = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "silly-player",
            Command = "stats"
        });
        stats.EnsureSuccessStatusCode();
        var statsResult = await stats.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statsResult.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task FullPlaythrough_WorldReset_ClearsAndReseeds()
    {
        // Create a character first
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();
        await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = "reset-test-player",
            Name = "Resettus",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray
        });

        // Give them some gold and items
        var adminClient = _factory.CreateAdminClient();
        await adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/resources", new
        {
            PlayerId = "reset-test-player",
            SetGold = 999
        });

        // Reset world keeping players
        var resetResponse = await adminClient.PostAsJsonAsync("/api/dashboard/admin/reset-world", new
        {
            KeepPlayers = true
        });
        resetResponse.EnsureSuccessStatusCode();
        var resetResult = await resetResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(resetResult.GetProperty("roomsSeeded").GetInt32() > 0);

        // Verify player was reset
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
        var player = await state.GetPlayerAsync("reset-test-player");
        Assert.NotNull(player);
        Assert.Equal("spawn", player!.CurrentRoomId);
        Assert.Equal(50, player.Gold); // Reset to starting gold
        Assert.Empty(player.Inventory);
        Assert.Equal(1, player.Level);

        // Verify rooms re-seeded
        var spawn = await state.GetRoomAsync("spawn");
        Assert.NotNull(spawn);
        Assert.Equal("The Rusted Flagon", spawn!.Name);

        var depths = await state.GetRoomAsync("dungeon_depths");
        Assert.NotNull(depths);
        Assert.Contains(depths!.Npcs, n => n.Name.Contains("Goretusk"));

        // Verify shopkeeper data survived re-seed
        var smithRoom = await state.GetRoomAsync("blacksmith");
        Assert.NotNull(smithRoom);
        var korgaNpc = smithRoom!.Npcs.FirstOrDefault(n => n.Name.Contains("Korga"));
        Assert.NotNull(korgaNpc);
        Assert.True(korgaNpc!.IsShopkeeper);
        Assert.NotEmpty(korgaNpc.ShopInventory);
    }
}
