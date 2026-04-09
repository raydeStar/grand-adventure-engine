using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// B04 — Leveling &amp; XP test coverage.
/// Verifies the full leveling flow: earn XP → hit threshold → level up → stats increase.
/// </summary>
public class LevelingXpTests
{
    private const string PlayerId = "test-player-1";

    // ── XP Threshold ─────────────────────────────────────────────

    [Fact]
    public void LevelUp_TriggersAtCorrectXpThreshold()
    {
        // Level 1→2 requires BaseXpPerLevel * 1 = 100 XP
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 100);

        var result = engine.CheckAndApplyLevelUp(player);

        Assert.NotNull(result);
        Assert.Equal(2, player.Level);
        Assert.Equal(0, player.Xp); // 100 consumed
    }

    [Fact]
    public void LevelUp_DoesNotTriggerBelowThreshold()
    {
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 99);

        var result = engine.CheckAndApplyLevelUp(player);

        Assert.Null(result);
        Assert.Equal(1, player.Level);
        Assert.Equal(99, player.Xp);
    }

    [Fact]
    public void XpThreshold_ScalesWithLevel()
    {
        var engine = CreateEngine(new InMemoryStateManager());

        // Level 1→2: 100 XP
        var p1 = CreatePlayer(level: 1, xp: 100);
        engine.CheckAndApplyLevelUp(p1);
        Assert.Equal(2, p1.Level);

        // Level 5→6: 500 XP
        var p5 = CreatePlayer(level: 5, xp: 500);
        engine.CheckAndApplyLevelUp(p5);
        Assert.Equal(6, p5.Level);

        // Level 10→11: 1000 XP
        var p10 = CreatePlayer(level: 10, xp: 1000);
        engine.CheckAndApplyLevelUp(p10);
        Assert.Equal(11, p10.Level);

        // Level 5→6: 499 XP NOT enough
        var p5b = CreatePlayer(level: 5, xp: 499);
        engine.CheckAndApplyLevelUp(p5b);
        Assert.Equal(5, p5b.Level);
    }

    // ── HP/MP Scaling ────────────────────────────────────────────

    [Fact]
    public void LevelUp_HpAndMpIncreasePerConfig()
    {
        // Con=10, Int=10 → modifiers 0 → baseHp=20, baseMp=10
        // Level 2: MaxHp = 20 * (1 + 0.10*1) = 22, MaxMp = 10 * (1 + 0.10*1) = 11
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 100);
        player.Con = 10;
        player.Int = 10;

        engine.CheckAndApplyLevelUp(player);

        Assert.Equal(2, player.Level);
        Assert.Equal(22, player.MaxHp);
        Assert.Equal(11, player.MaxMp);
    }

    [Fact]
    public void LevelUp_HpScalesWithConstitution()
    {
        // Con=14 → modifier +2 → baseHp = 20 + 2*2 = 24
        // Level 2: MaxHp = 24 * (1 + 0.10*1) = 26 (truncated)
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 100);
        player.Con = 14;
        player.Int = 10;

        engine.CheckAndApplyLevelUp(player);

        Assert.Equal(2, player.Level);
        Assert.Equal(26, player.MaxHp); // (int)(24 * 1.10) = 26
    }

    // ── Multiple Level-Ups ───────────────────────────────────────

    [Fact]
    public void MultipleLevelUps_InSingleXpGrant()
    {
        // Level 1 with 300 XP: costs 100 (→2), then 200 (→3), 0 remaining
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 300);

        var result = engine.CheckAndApplyLevelUp(player);

        Assert.NotNull(result);
        Assert.Equal(3, player.Level);
        Assert.Equal(0, player.Xp); // 300 - 100 - 200 = 0
    }

    [Fact]
    public void MultipleLevelUps_WithLeftoverXp()
    {
        // Level 1 with 350 XP: costs 100 (→2), 200 (→3), 50 remaining (not enough for 300)
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 350);

        engine.CheckAndApplyLevelUp(player);

        Assert.Equal(3, player.Level);
        Assert.Equal(50, player.Xp);
    }

    // ── Level-Up Healing ─────────────────────────────────────────

    [Fact]
    public void LevelUp_HealsToFull()
    {
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 1, xp: 100);
        player.Hp = 5;
        player.Mp = 2;

        engine.CheckAndApplyLevelUp(player);

        Assert.Equal(player.MaxHp, player.Hp);
        Assert.Equal(player.MaxMp, player.Mp);
    }

    // ── Level Cap ────────────────────────────────────────────────

    [Fact]
    public void LevelCap_PreventsFurtherLeveling()
    {
        // MaxLevel = 20 by default
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 20, xp: 9999);

        var result = engine.CheckAndApplyLevelUp(player);

        Assert.Null(result);
        Assert.Equal(20, player.Level);
        Assert.Equal(9999, player.Xp); // XP not consumed at cap
    }

    [Fact]
    public void MultipleLevelUps_StopsAtCap()
    {
        // Level 19 with massive XP: should only level to 20, not beyond
        var engine = CreateEngine(new InMemoryStateManager());
        var player = CreatePlayer(level: 19, xp: 50000);

        engine.CheckAndApplyLevelUp(player);

        Assert.Equal(20, player.Level);
        // 50000 - 1900 (level 19→20) = 48100 remaining
        Assert.Equal(48100, player.Xp);
    }

    // ── Combat XP ────────────────────────────────────────────────

    [Fact]
    public async Task CombatXp_CorrectAmountFromEnemyLevel()
    {
        // Enemy level 5 → XP gain = 5 * 10 = 50
        var state = await CreateStateAsync();
        var room = (await state.GetRoomAsync("spawn"))!;
        var enemy = new Npc
        {
            Id = "bandit", Name = "Bandit", Level = 5,
            Hp = 1, MaxHp = 30, Defense = 5,
            AttackBonus = 2, DamageDice = "1d4",
            DispositionState = new NpcDispositionState()
        };
        room.Npcs.Add(enemy);
        await state.SaveRoomAsync(room);

        var combat = new CombatState
        {
            WorldId = WorldDefaults.DefaultWorldId,
            RoomId = "spawn",
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1,
            IsActive = true,
            TurnOrder =
            [
                new CombatParticipant { Id = PlayerId, Name = "Hero", IsPlayer = true, Hp = 20, MaxHp = 20 },
                new CombatParticipant { Id = "bandit", Name = "Bandit", IsPlayer = false, Hp = 1, MaxHp = 30 }
            ]
        };
        await state.SaveCombatStateAsync(combat);

        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Xp = 0;
        player.Interaction = new InteractionState { Mode = InteractionMode.Combat, Target = "Bandit" };
        await state.SavePlayerAsync(player);

        // Mock dice: player crits for lethal damage
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Total = 20, IsCritical = true, IndividualRolls = [20] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Total = 50, IndividualRolls = [50] });
        // Loot check: roll high to skip gold drop (EnemyDropChance = 0.6 → need > 60)
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Total = 99, IndividualRolls = [99] });

        var engine = CreateEngineWithMockedDice(state, dice.Object);
        var action = engine.ParseCommand(PlayerId, "attack bandit");
        await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(50, player.Xp); // Level 5 * 10 = 50 XP
    }

    [Fact]
    public async Task CombatVictory_TriggersLevelUp()
    {
        // Player at level 1 with 60 XP, kills level 5 enemy (50 XP) → 110 XP total → level up at 100
        var state = await CreateStateAsync();
        var room = (await state.GetRoomAsync("spawn"))!;
        var enemy = new Npc
        {
            Id = "bandit", Name = "Bandit", Level = 5,
            Hp = 1, MaxHp = 30, Defense = 5,
            AttackBonus = 2, DamageDice = "1d4",
            DispositionState = new NpcDispositionState()
        };
        room.Npcs.Add(enemy);
        await state.SaveRoomAsync(room);

        var combat = new CombatState
        {
            WorldId = WorldDefaults.DefaultWorldId,
            RoomId = "spawn",
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1,
            IsActive = true,
            TurnOrder =
            [
                new CombatParticipant { Id = PlayerId, Name = "Hero", IsPlayer = true, Hp = 20, MaxHp = 20 },
                new CombatParticipant { Id = "bandit", Name = "Bandit", IsPlayer = false, Hp = 1, MaxHp = 30 }
            ]
        };
        await state.SaveCombatStateAsync(combat);

        var player = (await state.GetPlayerAsync(PlayerId))!;
        player.Xp = 60; // 60 + 50 from kill = 110, threshold is 100
        player.Interaction = new InteractionState { Mode = InteractionMode.Combat, Target = "Bandit" };
        await state.SavePlayerAsync(player);

        // Mock dice: player crits for lethal damage
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Total = 20, IsCritical = true, IndividualRolls = [20] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Total = 50, IndividualRolls = [50] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Total = 99, IndividualRolls = [99] });

        var engine = CreateEngineWithMockedDice(state, dice.Object);
        var action = engine.ParseCommand(PlayerId, "attack bandit");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        player = (await state.GetPlayerAsync(PlayerId))!;
        Assert.Equal(2, player.Level);
        Assert.Equal(10, player.Xp); // 110 - 100 = 10 remaining
        Assert.Contains("LEVEL UP", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static PlayerCharacter CreatePlayer(int level = 1, int xp = 0) => new()
    {
        Id = PlayerId,
        Name = "Hero",
        Race = "Human",
        Class = "Warrior",
        Level = level,
        Xp = xp,
        CurrentRoomId = "spawn",
        Hp = 20,
        MaxHp = 20,
        Mp = 10,
        MaxMp = 10,
        Str = 12,
        Dex = 10,
        Con = 10,
        Int = 10,
        Wis = 10,
        Cha = 10,
        Gold = 50
    };

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

    private static async Task<InMemoryStateManager> CreateStateAsync()
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
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
            Name = "Hero",
            Race = "Human",
            Class = "Warrior",
            Level = 1,
            CurrentRoomId = "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10,
            Str = 12,
            Dex = 10,
            Con = 10,
            Int = 10,
            Wis = 10,
            Cha = 10,
            Gold = 50
        });

        return stateManager;
    }
}
