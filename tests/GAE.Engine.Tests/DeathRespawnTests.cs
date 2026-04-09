using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class DeathRespawnTests
{
    private const string PlayerId = "test-player";

    [Fact]
    public async Task DeadPlayer_AnyAction_TriggersAutoRespawn()
    {
        var state = await CreateStateAsync();
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.True(player.IsAlive);
        Assert.Equal(player.MaxHp, player.Hp);
        Assert.Equal(player.MaxMp, player.Mp);
        Assert.Equal("spawn", player.CurrentRoomId);
        Assert.Contains("defeated", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Respawn_LocationIsSpawnRoom()
    {
        var state = await CreateStateAsync();

        // Move player to a different room
        await state.SaveRoomAsync(new Room { Id = "dungeon", Name = "Dark Dungeon", Description = "A grim place." });
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.CurrentRoomId = "dungeon";
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal("spawn", player.CurrentRoomId);
    }

    [Fact]
    public async Task Respawn_HpAndMpFullyRestored()
    {
        var state = await CreateStateAsync();
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.Mp = 0;
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(player.MaxHp, player.Hp);
        Assert.Equal(player.MaxMp, player.Mp);
    }

    [Fact]
    public async Task Respawn_GoldPreserved()
    {
        var state = await CreateStateAsync();
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.Gold = 42;
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(42, player.Gold);
    }

    [Fact]
    public async Task Respawn_InventoryPreserved()
    {
        var state = await CreateStateAsync();
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.Inventory.Add(new InventoryItem { Id = "sword", Name = "Iron Sword", Quantity = 1 });
        player.Inventory.Add(new InventoryItem { Id = "potion", Name = "Health Potion", Quantity = 3 });
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(2, player.Inventory.Count);
        Assert.Contains(player.Inventory, i => i.Id == "sword");
        Assert.Contains(player.Inventory, i => i.Id == "potion" && i.Quantity == 3);
    }

    [Fact]
    public async Task Respawn_CombatStateCleared()
    {
        var state = await CreateStateAsync();
        var room = (await state.GetRoomAsync("spawn"))!;
        room.Npcs.Add(new Npc { Id = "goblin", Name = "Goblin", Hp = 10, MaxHp = 10 });
        await state.SaveRoomAsync(room);

        // Set up combat state
        var combat = new CombatState
        {
            WorldId = WorldDefaults.DefaultWorldId,
            RoomId = "spawn",
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1,
            IsActive = true,
            TurnOrder =
            [
                new CombatParticipant { Id = PlayerId, Name = "Hero", IsPlayer = true, Hp = 1, MaxHp = 20 },
                new CombatParticipant { Id = "goblin", Name = "Goblin", IsPlayer = false, Hp = 10, MaxHp = 10 }
            ]
        };
        await state.SaveCombatStateAsync(combat);

        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.Interaction = new InteractionState { Mode = InteractionMode.Combat, Target = "Goblin" };
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
    }

    [Fact]
    public async Task Respawn_WorksWithNpcsInSpawnRoom()
    {
        var spawnRoom = new Room
        {
            Id = "spawn",
            Name = "The Crossroads Inn",
            Description = "A weathered inn.",
            Npcs = [new Npc { Id = "barkeep", Name = "Barkeep", Personality = "Friendly" }],
            Items = [new InventoryItem { Id = "ale", Name = "Flat Ale", Quantity = 1 }]
        };
        var state = await CreateStateAsync(spawnRoom);

        // Put player in a different room and kill them
        await state.SaveRoomAsync(new Room { Id = "pit", Name = "The Pit", Description = "A dark pit." });
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.CurrentRoomId = "pit";
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal("spawn", player.CurrentRoomId);
        Assert.True(player.IsAlive);

        // Spawn room NPCs and items should still be there
        var room = (await state.GetRoomAsync("spawn"))!;
        Assert.Single(room.Npcs);
        Assert.Single(room.Items);
    }

    [Fact]
    public async Task MultipleDeaths_AllRespawnCorrectly()
    {
        var state = await CreateStateAsync();
        var engine = CreateEngine(state);

        for (var i = 0; i < 3; i++)
        {
            var player = (await state.GetPlayerAsync(PlayerId))!;
            player.Hp = 0;
            player.CurrentRoomId = "spawn";
            player.Gold = 100 + i;
            await state.SavePlayerAsync(player);

            var action = engine.ParseCommand(PlayerId, "look");
            var result = await engine.ProcessActionAsync(PlayerId, action);

            player = (await state.GetPlayerAsync(PlayerId))!;
            Assert.True(player.IsAlive, $"Player should be alive after death #{i + 1}");
            Assert.Equal(player.MaxHp, player.Hp);
            Assert.Equal(100 + i, player.Gold);
            Assert.Contains("defeated", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CombatDeath_EnemyKillsPlayer_TriggersRespawn()
    {
        var state = await CreateStateAsync();
        var room = (await state.GetRoomAsync("spawn"))!;
        var goblin = new Npc
        {
            Id = "goblin", Name = "Goblin", Hp = 50, MaxHp = 50,
            AttackBonus = 10, DamageDice = "1d6",
            DispositionState = new NpcDispositionState()
        };
        room.Npcs.Add(goblin);
        await state.SaveRoomAsync(room);

        // Set up combat state
        var combat = new CombatState
        {
            WorldId = WorldDefaults.DefaultWorldId,
            RoomId = "spawn",
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1,
            IsActive = true,
            TurnOrder =
            [
                new CombatParticipant { Id = PlayerId, Name = "Playwright Hero", IsPlayer = true, Hp = 1, MaxHp = 20 },
                new CombatParticipant { Id = "goblin", Name = "Goblin", IsPlayer = false, Hp = 50, MaxHp = 50 }
            ]
        };
        await state.SaveCombatStateAsync(combat);

        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 1; // One hit from death
        player.Interaction = new InteractionState { Mode = InteractionMode.Combat, Target = "Goblin" };
        await state.SavePlayerAsync(player);

        // Mock dice: player fumbles, enemy crits for lethal damage
        var dice = new Mock<IProbabilityEngine>();
        var attackCallCount = 0;
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(() =>
            {
                attackCallCount++;
                if (attackCallCount == 1)
                    // Player fumbles
                    return new DiceRoll { Total = 1, IsFumble = true, IndividualRolls = [1] };
                // Enemy crits
                return new DiceRoll { Total = 20, IsCritical = true, IndividualRolls = [20] };
            });
        // Enemy damage roll — enough to kill player at 1 HP
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Total = 10, IndividualRolls = [10] });

        var engine = CreateEngineWithMockedDice(state, dice.Object);
        var action = engine.ParseCommand(PlayerId, "attack goblin");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.True(player.IsAlive, "Player should be alive after respawn");
        Assert.Equal(player.MaxHp, player.Hp);
        Assert.Equal(player.MaxMp, player.Mp);
        Assert.Equal("spawn", player.CurrentRoomId);
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
        Assert.Contains("defeated", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Respawn_StateChangesIncludeHpMpRoom()
    {
        var state = await CreateStateAsync();
        await state.SaveRoomAsync(new Room { Id = "dungeon", Name = "Dungeon", Description = "Dark." });
        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Hp = 0;
        player.Mp = 3;
        player.CurrentRoomId = "dungeon";
        await state.SavePlayerAsync(player);

        var engine = CreateEngine(state);
        var action = engine.ParseCommand(PlayerId, "look");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.Contains(result.StateChanges, sc => sc.Property == "Hp" && sc.NewValue == "20");
        Assert.Contains(result.StateChanges, sc => sc.Property == "Mp" && sc.NewValue == "10");
        Assert.Contains(result.StateChanges, sc => sc.Property == "CurrentRoomId" && sc.NewValue == "spawn");
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static GameEngine CreateEngine(IStateManager stateManager)
    {
        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(stateManager, dice.Object, narrator.Object, parser, new GameRulesConfig(), NullLogger<GameEngine>.Instance);
    }

    private static GameEngine CreateEngineWithMockedDice(IStateManager stateManager, IProbabilityEngine dice)
    {
        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        narrator.Setup(s => s.ProcessCombatTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse { Success = true, Narration = "Combat narration." });
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(stateManager, dice, narrator.Object, parser, new GameRulesConfig(), NullLogger<GameEngine>.Instance);
    }

    private static async Task<InMemoryStateManager> CreateStateAsync(Room? room = null)
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(room ?? new Room
        {
            Id = "spawn",
            Name = "The Crossroads Inn",
            Description = "A weathered inn at the junction of three ancient roads.",
            Exits = new Dictionary<string, string>
            {
                ["north"] = "pine-road",
                ["south"] = "river-step"
            }
        });

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Playwright Hero",
            Race = "Human",
            Class = "Warrior",
            Level = 1,
            CurrentRoomId = room?.Id ?? "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10,
            Str = 12,
            Dex = 10,
            Con = 11,
            Int = 10,
            Wis = 10,
            Cha = 10,
            Gold = 0
        });

        return stateManager;
    }
}
