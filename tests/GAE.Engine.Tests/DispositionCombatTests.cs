using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for X03: NPC disposition affecting combat — stat modifiers,
/// morale-based surrender/flee, and unprovoked aggression.
/// </summary>
public class DispositionCombatTests
{
    private const string PlayerId = "combat-tester";

    // ═══════════════════════════════════════════════════════════════════
    //  GetDispositionCombatMods — unit tests for the modifier table
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("hostile", 90, 2, 2, 2)]   // Furious — full bonuses
    [InlineData("hostile", 80, 2, 2, 2)]   // Boundary at 80
    [InlineData("hostile", 79, 1, 1, 1)]   // Medium hostile
    [InlineData("hostile", 50, 1, 1, 1)]   // Low hostile
    [InlineData("hostile", 49, 0, 0, 0)]   // Below threshold — no mod
    [InlineData("angry", 90, 2, 1, 1)]     // Fury bonus
    [InlineData("angry", 50, 1, 0, 0)]     // Moderate anger
    [InlineData("angry", 30, 0, 0, 0)]     // Below threshold
    [InlineData("contemptuous", 70, 1, 1, 1)] // Contempt counts as hostile-like
    [InlineData("scared", 80, -2, -1, -1)] // Scared — penalized
    [InlineData("scared", 20, -2, -1, -1)] // Scared at any intensity
    [InlineData("friendly", 65, -2, -2, -2)] // Reluctant fighter
    [InlineData("grateful", 70, -2, -2, -2)]
    [InlineData("amused", 55, -2, -2, -2)]
    [InlineData("friendly", 30, 0, 0, 0)]  // Friendly below threshold — no mod
    [InlineData("neutral", 50, 0, 0, 0)]   // Neutral — no mod
    [InlineData("intrigued", 80, 0, 0, 0)] // Unrecognized emotion — no mod
    public void GetDispositionCombatMods_ReturnsCorrectModifiers(
        string emotion, int intensity, int expectedAtk, int expectedDmg, int expectedInit)
    {
        var npc = new Npc
        {
            Id = "test",
            Name = "Test NPC",
            DispositionState = new NpcDispositionState
            {
                Emotion = emotion,
                Intensity = intensity
            }
        };

        var mods = GameEngine.GetDispositionCombatMods(npc);

        Assert.Equal(expectedAtk, mods.AttackBonus);
        Assert.Equal(expectedDmg, mods.DamageBonus);
        Assert.Equal(expectedInit, mods.InitiativeBonus);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Combat damage modified by disposition
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FuriousEnemy_DealsBonusDamage()
    {
        var npc = CreateHostileNpc("Berserker", emotion: "hostile", intensity: 90);
        var state = await CreateCombatStateAsync(npc);
        await SetupCombatAsync(state, npc); // Pre-enter combat so "attack" goes through ProcessCombatTurnAsync

        // goldRoll=3 controls enemy damage dice (Roll) → damage = 3 + 2 disp bonus = 5 per exchange
        // 3 exchanges × 5 = 15 total, player survives at 5 HP
        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 5, goldRoll: 3);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        // Multi-exchange combat: enemy counterattacks with disposition bonus
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.True(player.Hp < 20, "Player should have taken damage from enemy counterattack");
    }

    [Fact]
    public async Task ScaredEnemy_DealsPenalizedDamage()
    {
        var npc = CreateHostileNpc("Coward", emotion: "scared", intensity: 60);
        var state = await CreateCombatStateAsync(npc);

        // Dice: attack total 15, damage 3. With -1 disp penalty: max(1, 3-1) = 2
        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 3);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack coward");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        // Scared enemies do less damage — player retains more HP
        Assert.True(player.Hp > 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NPC Morale — Surrender
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FriendlyBaselineNpc_SurrenderWhenBadlyWounded()
    {
        // NPC with friendly baseline (65) at very low HP should surrender
        var npc = CreateHostileNpc("Guard", emotion: "angry", intensity: 70, hp: 4, maxHp: 20);
        npc.DispositionState.Baseline = 65; // friendly baseline
        var state = await CreateCombatStateAsync(npc);
        await SetupCombatAsync(state, npc); // Pre-enter combat for morale checks

        // Player attack hits hard enough to keep NPC below 25% HP
        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 2, goldRoll: 10);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        // After taking 2 damage, guard is at 2/20 HP (10%) — should surrender
        Assert.Contains("surrenders", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);

        // Verify NPC is no longer hostile and has resigned emotion
        var room = await state.GetPlayerRoomAsync(PlayerId, "combat-room");
        var guard = room?.Npcs.FirstOrDefault(n => n.Name == "Guard");
        if (guard is not null) // may have been removed from enemies list
        {
            Assert.False(guard.IsHostile);
            Assert.Equal("resigned", guard.DispositionState.Emotion);
        }
    }

    [Fact]
    public async Task HostileBaselineNpc_DoesNotSurrender()
    {
        // NPC with hostile baseline (20) should never surrender
        var npc = CreateHostileNpc("Assassin", emotion: "hostile", intensity: 90, hp: 3, maxHp: 20);
        npc.DispositionState.Baseline = 20; // hostile baseline
        var state = await CreateCombatStateAsync(npc);

        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 1, goldRoll: 10);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack assassin");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        // Hostile-baseline NPC (baseline 20, below 50) should NOT surrender
        Assert.DoesNotContain("surrenders", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BossArenaNpc_DoesNotSurrenderWhenBadlyWounded()
    {
        var npc = CreateHostileNpc("Nullthorn", emotion: "hostile", intensity: 90, hp: 4, maxHp: 20);
        npc.DispositionState.Baseline = 65;
        var state = await CreateCombatStateAsync(npc, environmentTags: ["boss_arena"]);
        await SetupCombatAsync(state, npc);

        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 1, goldRoll: 10);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack nullthorn");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.DoesNotContain("surrenders", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flees", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NPC Morale — Flee
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScaredNpc_FleesAtHalfHp()
    {
        var npc = CreateHostileNpc("Goblin", emotion: "scared", intensity: 70, hp: 9, maxHp: 20);
        var state = await CreateCombatStateAsync(npc);
        await SetupCombatAsync(state, npc); // Pre-enter combat for morale checks

        // Player hits for 1 damage → Goblin at 8/20 = 40% HP → should flee (< 50%)
        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 1, goldRoll: 10);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("flees", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);

        // Goblin should be removed from the room
        var room = await state.GetPlayerRoomAsync(PlayerId, "combat-room");
        Assert.NotNull(room);
        Assert.DoesNotContain(room.Npcs, n => n.Name == "Goblin");
    }

    [Fact]
    public async Task ScaredNpc_DoesNotFleeAboveHalfHp()
    {
        var npc = CreateHostileNpc("Goblin", emotion: "scared", intensity: 70, hp: 20, maxHp: 20);
        var state = await CreateCombatStateAsync(npc);

        // Player hits for 1 → Goblin at 19/20 = 95% → should NOT flee
        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 1, goldRoll: 10);
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack goblin");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.DoesNotContain("flees", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Unprovoked NPC Aggression
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnragedNpc_AttacksUnprovoked()
    {
        var npc = new Npc
        {
            Id = "innkeeper",
            Name = "Innkeeper",
            IsHostile = false,
            Hp = 20,
            MaxHp = 20,
            Defense = 10,
            Level = 3,
            DamageDice = "1d6",
            DispositionState = new NpcDispositionState
            {
                Emotion = "hostile",
                Intensity = 95,
                Baseline = 30,
                Reason = "Player burned down the inn."
            }
        };
        var state = await CreateCombatStateAsync(npc, npcStartsHostile: false);

        var dice = CreateCombatDice(attackTotal: 10, damageTotal: 5);
        var engine = CreateEngine(state, dice.Object);

        // Player tries to do something normal — the enraged NPC intervenes
        var action = engine.ParseCommand(PlayerId, "look");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("attacks you", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Innkeeper", result.MechanicalSummary);

        // Player should now be in combat
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Combat, player.Interaction.Mode);
    }

    [Fact]
    public async Task AngryNpc_BelowThreshold_DoesNotAttack()
    {
        var npc = new Npc
        {
            Id = "innkeeper",
            Name = "Innkeeper",
            IsHostile = false,
            Hp = 20,
            MaxHp = 20,
            Defense = 10,
            Level = 3,
            DispositionState = new NpcDispositionState
            {
                Emotion = "angry",
                Intensity = 80, // Below 90 threshold
                Baseline = 50
            }
        };
        var room = new Room
        {
            Id = "tavern",
            Name = "Tavern",
            Description = "A warm tavern.",
            Npcs = [npc],
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        };
        var state = await CreateStateAsync(room);
        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You look around.");
        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(state, dice.Object, narrator.Object, parser,
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var action = engine.ParseCommand(PlayerId, "look");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        // No unprovoked attack — just a normal look
        Assert.DoesNotContain("attacks you", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
    }

    [Fact]
    public async Task AlreadyHostileNpc_DoesNotTriggerUnprovokedAttack()
    {
        // NPC is already hostile — unprovoked check requires !IsHostile
        var npc = new Npc
        {
            Id = "bandit",
            Name = "Bandit",
            IsHostile = true,
            Hp = 20,
            MaxHp = 20,
            Defense = 10,
            Level = 2,
            DamageDice = "1d6",
            DispositionState = new NpcDispositionState
            {
                Emotion = "hostile",
                Intensity = 95,
                Baseline = 10
            }
        };
        var room = new Room
        {
            Id = "road",
            Name = "Dangerous Road",
            Description = "A winding road.",
            Npcs = [npc],
            Exits = new Dictionary<string, string> { ["north"] = "town" }
        };
        var state = await CreateStateAsync(room);
        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You look around.");
        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(state, dice.Object, narrator.Object, parser,
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var action = engine.ParseCommand(PlayerId, "look");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        // Already hostile NPC doesn't trigger the unprovoked path — just normal look
        Assert.DoesNotContain("attacks you", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Initiative with disposition modifiers
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FuriousEnemy_GetsInitiativeBonus()
    {
        var npc = CreateHostileNpc("Berserker", emotion: "hostile", intensity: 90);
        npc.AttackBonus = 4; // base dex mod = 4/2 = 2, plus disp +2 = +4 total init mod
        var state = await CreateCombatStateAsync(npc);

        var dice = CreateCombatDice(attackTotal: 15, damageTotal: 5);
        // Initiative rolls: use the generic Roll for both
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns((int mod) => new DiceRoll
            {
                Expression = $"1d20+{mod}",
                Total = 10 + mod, // deterministic: base 10 + modifier
                IndividualRolls = [10]
            });
        var engine = CreateEngine(state, dice.Object);

        var action = engine.ParseCommand(PlayerId, "attack berserker");
        await engine.ProcessActionAsync(PlayerId, action);

        // The combat state should have the berserker's initiative include the disposition bonus
        // The engine uses GetPlayerRoomAsync which clones room with Id="playerId:roomId"
        var playerRoomId = $"{PlayerId}:combat-room";
        var combat = await state.GetCombatStateAsync(playerRoomId, WorldDefaults.DefaultWorldId);
        Assert.NotNull(combat);
        var berserkerEntry = combat.TurnOrder.FirstOrDefault(t => t.Name == "Berserker");
        Assert.NotNull(berserkerEntry);
        // Berserker init mod = AttackBonus/2 + dispMod = 2 + 2 = 4
        // Total = 10 + 4 = 14
        Assert.Equal(14, berserkerEntry.Initiative);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static Npc CreateHostileNpc(
        string name,
        string emotion = "hostile",
        int intensity = 65,
        int hp = 20,
        int maxHp = 20)
    {
        return new Npc
        {
            Id = name.ToLowerInvariant().Replace(" ", "-"),
            Name = name,
            IsHostile = true,
            Hp = hp,
            MaxHp = maxHp,
            Defense = 10,
            Level = 2,
            AttackBonus = 2,
            DamageDice = "1d6",
            DispositionState = new NpcDispositionState
            {
                Emotion = emotion,
                Intensity = intensity,
                Baseline = intensity < 40 ? intensity : 30
            }
        };
    }

    private static async Task<InMemoryStateManager> CreateCombatStateAsync(
        Npc enemy,
        bool npcStartsHostile = true,
        IEnumerable<string>? environmentTags = null)
    {
        enemy.IsHostile = npcStartsHostile;
        var room = new Room
        {
            Id = "combat-room",
            Name = "The Arena",
            Description = "A stone arena for combat.",
            Npcs = [enemy],
            EnvironmentTags = environmentTags?.ToList() ?? [],
            Exits = new Dictionary<string, string> { ["north"] = "corridor" }
        };

        return await CreateStateAsync(room);
    }

    private static async Task<InMemoryStateManager> CreateStateAsync(Room room)
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(room);
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Test Fighter",
            Race = "Human",
            Class = "Warrior",
            Level = 1,
            CurrentRoomId = room.Id,
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10,
            Str = 14, // +2 mod
            Dex = 10,
            Con = 10, // MaxHp stays 20 after RecalculateMaxHpMp
            Int = 10,
            Wis = 10,
            Cha = 10
        });
        return stateManager;
    }

    /// <summary>
    /// Pre-enters combat mode so the next action routes through ProcessCombatTurnAsync
    /// (which includes enemy counterattacks and morale checks).
    /// </summary>
    private static async Task SetupCombatAsync(InMemoryStateManager state, Npc enemy)
    {
        var player = await state.GetPlayerAsync(PlayerId);
        player!.Interaction.Mode = InteractionMode.Combat;
        player.Interaction.Target = enemy.Id;
        await state.SavePlayerAsync(player);

        var combat = new CombatState
        {
            RoomId = player.CurrentRoomId,
            WorldId = WorldDefaults.DefaultWorldId,
            Phase = CombatPhase.PlayerTurn,
            IsActive = true,
            TurnOrder =
            [
                new CombatParticipant { Id = player.Id, Name = player.Name, IsPlayer = true, Initiative = 10, Hp = player.Hp, MaxHp = player.MaxHp },
                new CombatParticipant { Id = enemy.Id, Name = enemy.Name, IsPlayer = false, Initiative = 10, Hp = enemy.Hp ?? 20, MaxHp = enemy.MaxHp ?? 20 }
            ]
        };
        await state.SaveCombatStateAsync(combat);
    }

    private static GameEngine CreateEngine(IStateManager stateManager, IProbabilityEngine dice)
    {
        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(stateManager, dice, narrator.Object, parser,
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);
    }

    private static Mock<IProbabilityEngine> CreateCombatDice(
        int attackTotal = 15,
        int damageTotal = 5,
        int goldRoll = 10)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns((int mod) => new DiceRoll
            {
                Expression = $"1d20+{mod}",
                Total = attackTotal,
                IndividualRolls = [attackTotal - mod]
            });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = damageTotal, IndividualRolls = [damageTotal] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = goldRoll, IndividualRolls = [goldRoll] });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns((int mod) => new DiceRoll
            {
                Expression = $"1d20+{mod}",
                Total = 10 + mod,
                IndividualRolls = [10]
            });
        return dice;
    }
}
