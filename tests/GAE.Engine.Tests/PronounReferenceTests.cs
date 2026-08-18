using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit.Abstractions;

namespace GAE.Engine.Tests;

/// <summary>
/// Players do not repeat nouns. Examining something and then acting on it with "that" or "it" must
/// work, or the game reads as though it stopped following the conversation.
/// </summary>
public class PronounReferenceTests
{
    private const string PlayerId = "referent-player";
    private readonly ITestOutputHelper _output;

    public PronounReferenceTests(ITestOutputHelper output) => _output = output;

    /// <summary>Shows how the parser reads the reported phrasings.</summary>
    [Fact]
    public void Preview_HowTheParserReadsFollowUps()
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);
        foreach (var input in new[]
                 {
                     "examine the discarded torch",
                     "ok i pick that up as well",
                     "pick that up",
                     "take it",
                     "grab that",
                     "pick up that",
                     "i take it too"
                 })
        {
            var a = engine.ParseCommand(PlayerId, input);
            _output.WriteLine($"{input,-34} -> {a.Type,-10} target={a.Target ?? "(none)"}");
        }
    }

    // ── The referent itself ──

    [Theory]
    [InlineData("it")]
    [InlineData("that")]
    [InlineData("this")]
    [InlineData("them")]
    [InlineData("that one")]
    [InlineData("the same")]
    public void PronounsAreRecognisedAsStandIns(string target)
        => Assert.True(InteractionState.IsPronounTarget(target));

    [Theory]
    [InlineData("torch")]
    [InlineData("discarded torch")]
    [InlineData("Mara the Barkeep")]
    public void RealNamesAreNotTreatedAsStandIns(string target)
        => Assert.False(InteractionState.IsPronounTarget(target));

    [Fact]
    public void RememberingSkipsPronounsSoTheyCannotResolveToThemselves()
    {
        var interaction = new InteractionState();

        interaction.RememberReferent("discarded torch");
        interaction.RememberReferent("it");

        Assert.Equal("discarded torch", interaction.LastReferent);
    }

    /// <summary>
    /// A referent must outlive a conversation ending: what you were just looking at is still the
    /// obvious "that".
    /// </summary>
    [Fact]
    public void TheReferentSurvivesAnInteractionReset()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };
        interaction.RememberReferent("discarded torch");

        interaction.Reset();

        Assert.Equal("discarded torch", interaction.LastReferent);
    }

    // ── End to end ──

    /// <summary>The reported bug, played through the engine.</summary>
    [Fact]
    public async Task ExamineThenPickThatUp_ActsOnTheExaminedItem()
    {
        var stateManager = await SeedAsync();
        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "examine the discarded torch"));
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "take that"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Contains(player!.Inventory, i => i.Name.Contains("Torch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TakeIt_ResolvesToTheLastExaminedItem()
    {
        var stateManager = await SeedAsync();
        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "look at the discarded torch"));
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "take it"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Contains(player!.Inventory, i => i.Name.Contains("Torch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A pronoun with nothing to point at must not invent a target.</summary>
    [Fact]
    public async Task APronounWithNoReferent_DoesNotGrabSomethingArbitrary()
    {
        var stateManager = await SeedAsync();
        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "take it"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Empty(player!.Inventory);
    }

    /// <summary>Naming something new must move the referent on.</summary>
    [Fact]
    public async Task TheReferentFollowsTheMostRecentlyNamedThing()
    {
        var stateManager = await SeedAsync();
        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "examine the discarded torch"));
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "examine the broken arrow"));
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "take that"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Contains(player!.Inventory, i => i.Name.Contains("Arrow", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(player.Inventory, i => i.Name.Contains("Torch", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The referent must be written to storage, not merely mutated in memory.
    ///
    /// The first version of this feature passed every other test here and still failed in the real
    /// game: action handlers save the player before the referent is recorded, so the change was lost.
    /// InMemoryStateManager hands back the same object reference, which hid the bug — this test
    /// re-reads through the state manager and compares against a detached copy to catch it.
    /// </summary>
    [Fact]
    public async Task TheReferentIsPersisted_NotJustHeldInMemory()
    {
        var stateManager = await SeedAsync();
        var engine = CreateEngine(stateManager, CreateNarrator().Object);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "examine the discarded torch"));

        // Round-trip through serialisation, the way the real store does, so an in-memory-only
        // mutation cannot masquerade as a saved one.
        var stored = await stateManager.GetPlayerAsync(PlayerId);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<PlayerCharacter>(
            System.Text.Json.JsonSerializer.Serialize(stored));

        Assert.False(string.IsNullOrWhiteSpace(roundTripped!.Interaction.LastReferent),
            "The referent did not survive serialisation, so it would be lost by a real state manager.");
        Assert.Contains("torch", roundTripped.Interaction.LastReferent!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Harness ──

    private static async Task<InMemoryStateManager> SeedAsync()
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "gate",
            Name = "Thornwall Eastern Gate",
            Description = "An iron-banded gate.",
            Items =
            [
                new InventoryItem { Id = "torch", Name = "Discarded Torch", Type = ItemType.Misc, Quantity = 1 },
                new InventoryItem { Id = "arrow", Name = "Broken Arrow", Type = ItemType.Misc, Quantity = 1 }
            ],
            Exits = new Dictionary<string, string> { ["west"] = "square" }
        });
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = "Meow Meow", Race = "Human", Class = "Wizard", Level = 5,
            CurrentRoomId = "gate", Hp = 28, MaxHp = 28, Mp = 16, MaxMp = 19,
            Str = 10, Dex = 10, Con = 10, Int = 14, Wis = 10, Cha = 12
        });
        return stateManager;
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [10], Total = 10 });
        dice.Setup(d => d.RollSkillCheck(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string s, int m) => new DiceRoll { Expression = "1d20", Purpose = s, IndividualRolls = [10], Modifier = m, Total = 10 + m });

        return new GameEngine(
            stateManager, dice.Object, narrator,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);
    }

    private static Mock<INarratorService> CreateNarrator()
    {
        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narration.");
        narrator.Setup(n => n.ProcessFreeFormAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "The world responds.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            });
        return narrator;
    }
}
