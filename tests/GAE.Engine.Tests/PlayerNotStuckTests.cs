using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Quality-of-life guarantees: a player must never be trapped in an interaction mode, and must
/// never be punished for asking the game a question.
/// </summary>
public class PlayerNotStuckTests
{
    private const string PlayerId = "qol-player";

    // ── Information is free ──

    /// <summary>
    /// Checking your own sheet mid-fight used to resolve a combat round: the player took a round of
    /// enemy attacks for looking in their own bag for a healing potion.
    /// </summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("stats")]
    [InlineData("help")]
    [InlineData("spellbook")]
    [InlineData("journal")]
    [InlineData("map")]
    [InlineData("look")]
    public async Task InfoLookupsInCombat_CostNoHpAndKeepCombatActive(string input)
    {
        var (stateManager, engine) = await StartCombatAsync();
        var before = await stateManager.GetPlayerAsync(PlayerId);
        var hpBefore = before!.Hp;

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(hpBefore, after!.Hp);
        Assert.Equal(InteractionMode.Combat, after.Interaction.Mode);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("stats")]
    [InlineData("help")]
    [InlineData("look")]
    public async Task InfoLookupsInTrading_KeepShopOpen(string input)
    {
        var (stateManager, engine) = await StartTradingAsync();

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Trading, after!.Interaction.Mode);
    }

    // ── Every mode has an exit ──

    /// <summary>A frightened player types "give up", not "flee". All of these must end the fight.</summary>
    [Theory]
    [InlineData("flee")]
    [InlineData("run")]
    [InlineData("run away")]
    [InlineData("escape")]
    [InlineData("retreat")]
    [InlineData("surrender")]
    [InlineData("give up")]
    [InlineData("yield")]
    [InlineData("stop fighting")]
    [InlineData("leave")]
    [InlineData("go north")]
    public async Task CombatAlwaysHasAnExit(string input)
    {
        var (stateManager, engine) = await StartCombatAsync();

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Explore, after!.Interaction.Mode);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("goodbye")]
    [InlineData("done")]
    [InlineData("nevermind")]
    [InlineData("stop shopping")]
    [InlineData("no thanks")]
    [InlineData("thats all")]
    public async Task TradingAlwaysHasAnExit(string input)
    {
        var (stateManager, engine) = await StartTradingAsync();

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Explore, after!.Interaction.Mode);
    }

    /// <summary>Fleeing should use the exit the player named, not an arbitrary one.</summary>
    [Fact]
    public async Task FleeingInANamedDirection_UsesThatExit()
    {
        var (stateManager, engine) = await StartCombatAsync(
            exits: new Dictionary<string, string> { ["north"] = "street", ["south"] = "cellar" });

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "go south"));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal("cellar", after!.CurrentRoomId);
    }

    // ── Exit intent outranks lookups ──

    /// <summary>
    /// Some farewells parse to an info command ("done" once mapped to the completed-quest list).
    /// Answering the lookup would leave the player still standing in the exchange they ended.
    /// </summary>
    [Fact]
    public async Task ExitPhraseThatLooksLikeALookup_StillExits()
    {
        var (stateManager, engine) = await StartTradingAsync();

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "done"));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Explore, after!.Interaction.Mode);
    }

    /// <summary>
    /// Short farewells must not match as a prefix — a sentence that merely begins with "nothing"
    /// is conversation, not a goodbye.
    /// </summary>
    [Fact]
    public async Task ShortExitWordInsideALongerSentence_DoesNotEndTrading()
    {
        var (stateManager, engine) = await StartTradingAsync();

        await engine.ProcessActionAsync(
            PlayerId,
            engine.ParseCommand(PlayerId, "nothing you sell interests me, show me the good stuff"));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Trading, after!.Interaction.Mode);
    }

    // ── Parser understands ordinary words ──

    [Theory]
    [InlineData("items", ActionType.Inventory)]
    [InlineData("gear", ActionType.Inventory)]
    [InlineData("commands", ActionType.Help)]
    [InlineData("what can i do", ActionType.Help)]
    [InlineData("options", ActionType.Help)]
    [InlineData("sheet", ActionType.Stats)]
    [InlineData("my stats", ActionType.Stats)]
    [InlineData("where am i", ActionType.Look)]
    [InlineData("exits", ActionType.Look)]
    [InlineData("where can i go", ActionType.Look)]
    [InlineData("what do you have", ActionType.Shop)]
    [InlineData("what are you selling", ActionType.Shop)]
    public void CommonPhrasings_ResolveToRealCommands(string input, ActionType expected)
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);

        Assert.Equal(expected, engine.ParseCommand(PlayerId, input).Type);
    }

    /// <summary>"done" must no longer be swallowed by the completed-quests command.</summary>
    [Fact]
    public void Done_IsNotParsedAsCompletedQuests()
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);

        Assert.NotEqual(ActionType.CompletedQuests, engine.ParseCommand(PlayerId, "done").Type);
    }

    // ── Harness ──

    private static async Task<(InMemoryStateManager, GameEngine)> StartCombatAsync(
        Dictionary<string, string>? exits = null)
    {
        var stateManager = new InMemoryStateManager();

        // A very tough, near-harmless enemy keeps the fight running for the whole test without
        // killing the player, so mode and HP assertions stay meaningful.
        await SeedAsync(stateManager, new Npc
        {
            Id = "goblin", Name = "Goblin", IsHostile = true, Level = 1,
            Hp = 400, MaxHp = 400, AttackBonus = 0, DamageDice = "1d2", Defense = 99
        }, exits);

        var engine = CreateEngine(stateManager, CreateNarrator().Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "attack goblin"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Combat, player!.Interaction.Mode);
        return (stateManager, engine);
    }

    private static async Task<(InMemoryStateManager, GameEngine)> StartTradingAsync()
    {
        var stateManager = new InMemoryStateManager();
        await SeedAsync(stateManager, new Npc
        {
            Id = "merchant", Name = "Merchant", IsShopkeeper = true, Disposition = "friendly",
            ShopInventory = [new InventoryItem { Id = "bread", Name = "Bread", Type = ItemType.Misc, Value = 2, Quantity = 5 }]
        });

        var engine = CreateEngine(stateManager, CreateNarrator().Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to merchant"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Trading, player!.Interaction.Mode);
        return (stateManager, engine);
    }

    private static async Task SeedAsync(
        InMemoryStateManager stateManager, Npc npc, Dictionary<string, string>? exits = null)
    {
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "The Rusted Flagon",
            Description = "A creaky tavern.",
            Npcs = [npc],
            Exits = exits ?? new Dictionary<string, string> { ["north"] = "street" }
        });

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = "Probe", Race = "Human", Class = "Warrior", Level = 3,
            CurrentRoomId = "tavern", Hp = 40, MaxHp = 40, Mp = 10, MaxMp = 10, Gold = 100,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10,
            Inventory = [new InventoryItem { Id = "potion", Name = "Healing Potion", Type = ItemType.Potion, Quantity = 2, IsConsumable = true }]
        });
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [1], Total = 1 });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string e, int m) => new DiceRoll { Expression = e, Purpose = "dmg", IndividualRolls = [1], Modifier = m, Total = 1 + m });
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns((int m) => new DiceRoll { Expression = "1d20", Purpose = "atk", IndividualRolls = [2], Modifier = m, Total = 2 + m });
        dice.Setup(d => d.RollSkillCheck(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string s, int m) => new DiceRoll { Expression = "1d20", Purpose = s, IndividualRolls = [10], Modifier = m, Total = 10 + m });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns((int m) => new DiceRoll { Expression = "1d20", Purpose = "init", IndividualRolls = [10], Modifier = m, Total = 10 + m });

        return new GameEngine(
            stateManager,
            dice.Object,
            narrator,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static Mock<INarratorService> CreateNarrator()
    {
        var narrator = new Mock<INarratorService>();

        narrator.Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc n, InteractionState _, string _, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"\"{n.Name} answers.\"",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Conversation }
            });

        narrator.Setup(s => s.ProcessCombatTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "Steel rings.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat }
            });

        narrator.Setup(s => s.ProcessFreeFormAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "The world responds.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            });

        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narration.");

        return narrator;
    }
}
