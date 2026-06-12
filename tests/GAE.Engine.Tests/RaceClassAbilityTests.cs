using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.Registry;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for X06: Race & class-specific abilities â€” trait assignment at creation,
/// passive damage resistance in combat, active ability execution, cooldowns, and level gating.
/// </summary>
public class RaceClassAbilityTests
{
    private const string PlayerId = "ability-tester";

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Character creation â€” trait & ability assignment
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task CreateCharacter_AssignsRaceTraits()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var concept = new CharacterConcept
        {
            PlayerDiscordId = PlayerId,
            Name = "Grok",
            Gender = "male",
            Race = "dwarf",
            Class = "fighter",
            Backstory = "A tough warrior."
        };

        var player = await engine.CreateCharacterFromConceptAsync(concept);

        Assert.Contains("armored_hide", player.ActiveTraits);
        Assert.Contains("warrior_culture", player.ActiveTraits);
        Assert.Contains("resilient", player.ActiveTraits);
        Assert.Equal(3, player.ActiveTraits.Count);
    }

    [Fact]
    public async Task CreateCharacter_AssignsClassAbilities_FilteredByLevel()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var concept = new CharacterConcept
        {
            PlayerDiscordId = PlayerId,
            Name = "Grok",
            Gender = "male",
            Race = "dwarf",
            Class = "fighter",
            Backstory = "A tough warrior."
        };

        var player = await engine.CreateCharacterFromConceptAsync(concept);

        // Level 1 â€” only shield_bash (unlock_level=1) should be available
        Assert.Contains("shield_bash", player.UnlockedAbilities);
        Assert.DoesNotContain("second_wind", player.UnlockedAbilities); // unlock_level=2
        Assert.DoesNotContain("rally", player.UnlockedAbilities);       // unlock_level=4
    }

    [Fact]
    public async Task CreateCharacter_StatBonusTrait_AppliedToStats()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        // Elf/Sylvar has "heightened_reflexes" trait â†’ StatBonus dex +1
        var concept = new CharacterConcept
        {
            PlayerDiscordId = PlayerId,
            Name = "Lira",
            Gender = "female",
            Race = "elf",
            Class = "fighter",
            Backstory = "A nimble warrior.",
            StatMethod = StatAllocationMethod.FlatValue // All stats start at 10
        };

        var player = await engine.CreateCharacterFromConceptAsync(concept);

        // FlatValue=10 for all, elf trait gives +1 dex â†’ 11
        Assert.Equal(11, player.Dex);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  CommandParser â€” ability command
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Theory]
    [InlineData("ability shield bash")]
    [InlineData("activate second wind")]
    [InlineData("technique backstab")]
    [InlineData("skill flurry of blows")]
    public void Parse_AbilityCommand_ReturnsAbilityAction(string input)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse(PlayerId, input);

        Assert.Equal(ActionType.Ability, action.Type);
        Assert.False(string.IsNullOrWhiteSpace(action.Target));
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Active ability execution
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task Ability_Heal_RestoresHp()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "cleric");
        player.Hp = 10;
        player.MaxHp = 30;
        player.Mp = 10;
        player.MaxMp = 20;
        player.UnlockedAbilities = ["divine_light"];
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability divine light");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Restored", result.MechanicalSummary);

        var updated = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(updated);
        Assert.True(updated.Hp > 10, "HP should increase from healing");
        Assert.True(updated.Mp < 10, "MP should decrease from ability cost");
    }

    [Fact]
    public async Task Ability_Damage_DealsDamageToEnemy()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Mp = 5;
        player.MaxMp = 10;
        player.UnlockedAbilities = ["shield_bash"];
        await state.SavePlayerAsync(player);

        var room = new Room
        {
            Id = "spawn",
            Name = "Arena",
            Npcs = [new Npc { Id = "goblin", Name = "Goblin", Hp = 20, MaxHp = 20, DamageDice = "1d4" }]
        };
        await state.SaveRoomAsync(room);

        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });

        var engine = CreateEngine(state, dice: dice.Object, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability shield bash");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Dealt", result.MechanicalSummary);
        Assert.Contains("Goblin", result.MechanicalSummary);

        var updatedRoom = await state.GetPlayerRoomAsync(PlayerId, "spawn");
        var goblin = updatedRoom?.Npcs.First(n => n.Name == "Goblin");
        Assert.NotNull(goblin);
        Assert.True(goblin.Hp < 20, "Goblin should have taken damage");
    }

    [Fact]
    public async Task Ability_Buff_AppliesStatusEffect()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Level = 4;
        player.Mp = 10;
        player.MaxMp = 10;
        player.UnlockedAbilities = ["rally"];
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability rally");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("STR", result.MechanicalSummary);

        var updated = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(updated);
        Assert.Contains(updated.StatusEffects, se => se.Name == "Rally");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Ability validation: cooldowns, MP, level gating
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public async Task Ability_OnCooldown_ReturnsFailure()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Mp = 10;
        player.MaxMp = 10;
        player.UnlockedAbilities = ["shield_bash"];
        player.AbilityCooldowns["shield_bash"] = 2;
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability shield bash");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("cooldown", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ability_InsufficientMp_ReturnsFailure()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "cleric");
        player.Mp = 0;
        player.MaxMp = 10;
        player.UnlockedAbilities = ["divine_light"];
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability divine light");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("MP", result.MechanicalSummary);
    }

    [Fact]
    public async Task Ability_LevelTooLow_ReturnsFailure()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Level = 1;
        player.Mp = 10;
        player.MaxMp = 10;
        player.UnlockedAbilities = ["second_wind"]; // requires level 2
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability second wind");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("level", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ability_NotUnlocked_ReturnsFailure()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Mp = 10;
        player.MaxMp = 10;
        // UnlockedAbilities is empty
        await state.SavePlayerAsync(player);
        await SaveSpawnRoom(state);

        var engine = CreateEngine(state, registry: registry);

        var action = engine.ParseCommand(PlayerId, "ability shield bash");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("haven't learned", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Cooldown ticking
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void TickAbilityCooldowns_DecrementsAndRemovesExpired()
    {
        var player = CreatePlayer();
        player.AbilityCooldowns["shield_bash"] = 2;
        player.AbilityCooldowns["second_wind"] = 1;

        GameEngine.TickAbilityCooldowns(player);

        Assert.Equal(1, player.AbilityCooldowns["shield_bash"]);
        Assert.DoesNotContain("second_wind", (IDictionary<string, int>)player.AbilityCooldowns);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Passive trait: damage resistance in combat
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void ApplyTraitDamageResistance_ReducesDamage()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        // Dwarf/Drakari has "armored_hide" â†’ DamageResistance physical 2
        var player = CreatePlayer(race: "dwarf");
        player.ActiveTraits = ["armored_hide"];

        int reduced = engine.ApplyTraitDamageResistance(player, 10, "physical");

        Assert.Equal(8, reduced); // 10 - 2 = 8
    }

    [Fact]
    public void ApplyTraitDamageResistance_WrongType_NoDamageReduction()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var player = CreatePlayer(race: "dwarf");
        player.ActiveTraits = ["armored_hide"]; // physical resistance only

        int reduced = engine.ApplyTraitDamageResistance(player, 10, "fire");

        Assert.Equal(10, reduced); // No reduction for fire
    }

    [Fact]
    public void ApplyTraitDamageResistance_MinimumOne()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var player = CreatePlayer(race: "dwarf");
        player.ActiveTraits = ["armored_hide", "resilient"]; // physical 2 + poison 3

        int reduced = engine.ApplyTraitDamageResistance(player, 1, "physical");

        Assert.Equal(1, reduced); // Minimum 1 even with resistance
    }

    [Fact]
    public void ApplyTraitDamageResistance_StacksMultipleTraits()
    {
        var registry = BuildRegistryWithStackingResistance();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var player = CreatePlayer(race: "test_race");
        player.ActiveTraits = ["trait_a", "trait_b"];

        // trait_a = 2 physical, trait_b = 3 physical â†’ total 5 reduction
        int reduced = engine.ApplyTraitDamageResistance(player, 10, "physical");

        Assert.Equal(5, reduced); // 10 - 5 = 5
    }

    [Fact]
    public async Task Combat_DamageResistance_ReducesEnemyDamage()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();

        var npc = new Npc
        {
            Id = "goblin",
            Name = "Goblin",
            Hp = 50,
            MaxHp = 50,
            AttackBonus = 10,
            DamageDice = "1d6",
            DispositionState = new NpcDispositionState { Emotion = "hostile", Intensity = 50 }
        };

        var player = CreatePlayer(race: "dwarf", className: "fighter");
        player.Hp = 20;
        player.MaxHp = 20;
        player.ActiveTraits = ["armored_hide"]; // physical resistance 2
        player.Interaction = new InteractionState { Mode = InteractionMode.Combat, Target = "Goblin" };
        await state.SavePlayerAsync(player);

        var room = new Room
        {
            Id = "spawn",
            Name = "Arena",
            Npcs = [npc]
        };
        await state.SaveRoomAsync(room);

        var combat = new CombatState
        {
            Id = "combat-1",
            RoomId = "spawn",
            WorldId = WorldDefaults.DefaultWorldId,
            TurnOrder = [
                new CombatParticipant { Id = PlayerId, Name = "Grok", IsPlayer = true },
                new CombatParticipant { Id = "goblin", Name = "Goblin", IsPlayer = false }
            ]
        };
        await state.SaveCombatStateAsync(combat);

        // Dice: attack roll 15, damage 5 (without resistance would deal 5, with dwarf armored_hide should deal 3)
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20+5", Total = 15, IndividualRolls = [10] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = 10, IndividualRolls = [10] });

        var engine = CreateEngine(state, dice: dice.Object, registry: registry);

        var action = engine.ParseCommand(PlayerId, "attack goblin");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        // Player should have taken damage, but less than 5 per hit due to resistance
        var updated = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(updated);
        // With 3 exchanges and resistance, player takes (5-2)=3 per hit
        Assert.True(updated.Hp < 20, "Player should have taken damage");
        // Without resistance: 5 Ã— 3 = 15 â†’ HP 5. With resistance: 3 Ã— 3 = 9 â†’ HP 11.
        Assert.True(updated.Hp > 5, "Damage resistance should reduce total damage taken");
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  AssignRaceTraits / AssignClassAbilities â€” unit tests
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    [Fact]
    public void AssignRaceTraits_UnknownRace_NoEffect()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var player = CreatePlayer(race: "unknown_race");
        engine.AssignRaceTraits(player);

        Assert.Empty(player.ActiveTraits);
    }

    [Fact]
    public void AssignClassAbilities_HigherLevel_UnlocksMore()
    {
        var registry = BuildRegistry();
        var state = new InMemoryStateManager();
        var engine = CreateEngine(state, registry: registry);

        var player = CreatePlayer(className: "fighter");
        player.Level = 4;
        engine.AssignClassAbilities(player);

        // Level 4 should unlock shield_bash (L1), second_wind (L2), rally (L4)
        Assert.Contains("shield_bash", player.UnlockedAbilities);
        Assert.Contains("second_wind", player.UnlockedAbilities);
        Assert.Contains("rally", player.UnlockedAbilities);
        Assert.Equal(3, player.UnlockedAbilities.Count);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    //  Helpers
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private static PlayerCharacter CreatePlayer(string race = "dwarf", string className = "fighter")
    {
        return new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Grok",
            Race = race,
            Class = className,
            CurrentRoomId = "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10,
            Level = 1,
            Str = 14,
            Dex = 12,
            Con = 14,
            Int = 10,
            Wis = 10,
            Cha = 10,
            Luck = 10
        };
    }

    private static async Task SaveSpawnRoom(InMemoryStateManager state)
    {
        await state.SaveRoomAsync(new Room { Id = "spawn", Name = "Spawn Room" });
    }

    private static GameEngine CreateEngine(
        IStateManager stateManager,
        IProbabilityEngine? dice = null,
        IContentRegistryService? registry = null)
    {
        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        narrator.Setup(n => n.GenerateBackstoryAsync(It.IsAny<CharacterConcept>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("A warrior's tale.");

        var mockDice = dice ?? CreateDefaultDice().Object;
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);

        return new GameEngine(
            stateManager,
            mockDice,
            narrator.Object,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance,
            registry: registry);
    }

    private static Mock<IProbabilityEngine> CreateDefaultDice()
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = 15, IndividualRolls = [15] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = 10, IndividualRolls = [10] });
        dice.Setup(d => d.RollStatArray())
            .Returns([10, 10, 10, 10, 10, 10, 10]);
        return dice;
    }

    private static ContentRegistryService BuildRegistry()
    {
        var registry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);

        registry.Races.Register(new RaceDefinition
        {
            Id = "dwarf",
            Name = "Drakari",
            StatBonuses = new Dictionary<string, int> { ["con"] = 2, ["str"] = 1 },
            Traits = ["Armored Hide", "Warrior Culture", "Resilient"],
            TraitEffects =
            [
                new TraitDefinition { Id = "armored_hide", Name = "Armored Hide", Effect = TraitEffectType.DamageResistance, TargetType = "physical", Value = 2 },
                new TraitDefinition { Id = "warrior_culture", Name = "Warrior Culture", Effect = TraitEffectType.Narrative },
                new TraitDefinition { Id = "resilient", Name = "Resilient", Effect = TraitEffectType.DamageResistance, TargetType = "poison", Value = 3 }
            ]
        });

        registry.Races.Register(new RaceDefinition
        {
            Id = "elf",
            Name = "Sylvar",
            StatBonuses = new Dictionary<string, int> { ["dex"] = 2, ["int"] = 1 },
            Traits = ["Keen Senses", "Heightened Reflexes"],
            TraitEffects =
            [
                new TraitDefinition { Id = "keen_senses", Name = "Keen Senses", Effect = TraitEffectType.SkillAdvantage, TargetType = "wis", Value = 1 },
                new TraitDefinition { Id = "heightened_reflexes", Name = "Heightened Reflexes", Effect = TraitEffectType.StatBonus, TargetType = "dex", Value = 1 }
            ]
        });

        registry.Classes.Register(new ClassDefinition
        {
            Id = "fighter",
            Name = "Knight",
            HitDie = "d10",
            PrimaryStat = "str",
            SecondaryStat = "con",
            Abilities =
            [
                new ClassAbility { Id = "shield_bash", Name = "Shield Bash", UnlockLevel = 1, MpCost = 0, CooldownTurns = 3, Effect = AbilityEffectType.Damage, DamageDice = "1d6" },
                new ClassAbility { Id = "second_wind", Name = "Second Wind", UnlockLevel = 2, MpCost = 0, CooldownTurns = 5, Effect = AbilityEffectType.Heal, HealAmount = 8 },
                new ClassAbility { Id = "rally", Name = "Rally", UnlockLevel = 4, MpCost = 2, CooldownTurns = 4, Effect = AbilityEffectType.Buff, TargetStat = "str", BuffValue = 2, Duration = 3 }
            ]
        });

        registry.Classes.Register(new ClassDefinition
        {
            Id = "cleric",
            Name = "Dawn Cleric",
            HitDie = "d8",
            PrimaryStat = "wis",
            SecondaryStat = "con",
            BaseMpBonus = 8,
            CanCastSpells = true,
            Abilities =
            [
                new ClassAbility { Id = "divine_light", Name = "Divine Light", UnlockLevel = 1, MpCost = 2, CooldownTurns = 3, Effect = AbilityEffectType.Heal, HealAmount = 10 },
                new ClassAbility { Id = "holy_smite", Name = "Holy Smite", UnlockLevel = 3, MpCost = 4, CooldownTurns = 4, Effect = AbilityEffectType.Damage, DamageDice = "2d6" }
            ]
        });

        return registry;
    }

    private static ContentRegistryService BuildRegistryWithStackingResistance()
    {
        var registry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);

        registry.Races.Register(new RaceDefinition
        {
            Id = "test_race",
            Name = "Test Race",
            TraitEffects =
            [
                new TraitDefinition { Id = "trait_a", Name = "Trait A", Effect = TraitEffectType.DamageResistance, TargetType = "physical", Value = 2 },
                new TraitDefinition { Id = "trait_b", Name = "Trait B", Effect = TraitEffectType.DamageResistance, TargetType = "physical", Value = 3 }
            ]
        });

        return registry;
    }
}
