using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Long conversations were the worst case, not the best. The context window is a fixed-size queue, so
/// the earliest turns fell out — and that is where commitments are made. These tests plant a promise
/// early, bury it under far more turns than the window holds, and assert it is still honoured.
/// </summary>
public class LongConversationMemoryTests
{
    private const string PlayerId = "long-talker";

    // ── The window itself ──

    [Fact]
    public void ACommitmentSurvivesFarMoreTurnsThanTheWindowHolds()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation, Target = "Pete" };

        interaction.AppendContext("Pete: buy me a drink an' I'll tell you somethin' nobody else will.");
        for (var turn = 0; turn < InteractionState.MaxContextEntries * 3; turn++)
            interaction.AppendContext($"Player: idle chatter number {turn}.");

        // Gone from the verbatim window, as designed...
        Assert.DoesNotContain(interaction.Context, line => line.Contains("nobody else will", StringComparison.OrdinalIgnoreCase));
        // ...but retained where it matters.
        Assert.Contains(interaction.PinnedContext, line => line.Contains("nobody else will", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TurnsThatAgeOutAreSummarisedRatherThanDiscarded()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };

        for (var turn = 0; turn < InteractionState.MaxContextEntries + 5; turn++)
            interaction.AppendContext($"Player: asked about the sunken vault, question {turn}.");

        Assert.False(string.IsNullOrWhiteSpace(interaction.RunningSummary));
        Assert.Contains("sunken vault", interaction.RunningSummary!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The window and summary are prompt budget, so neither may grow without bound.</summary>
    [Fact]
    public void NeitherTheWindowNorTheSummaryGrowsWithoutBound()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };

        for (var turn = 0; turn < 400; turn++)
            interaction.AppendContext($"Player: a reasonably wordy exchange about matters of consequence, number {turn}.");

        Assert.True(interaction.Context.Count <= InteractionState.MaxContextEntries);
        Assert.True(interaction.RunningSummary!.Length <= InteractionState.MaxRunningSummaryLength + 32,
            $"Running summary grew to {interaction.RunningSummary.Length} characters.");
        Assert.True(interaction.PinnedContext.Count <= InteractionState.MaxPinnedEntries);
    }

    [Fact]
    public void OrdinaryChatterIsNotPinned()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };

        interaction.AppendContext("Player: what's the weather been like?");
        interaction.AppendContext("Mara: wet, and then wetter.");

        Assert.Empty(interaction.PinnedContext);
    }

    [Theory]
    [InlineData("Pete: buy me a drink an' I'll tell you what I saw.")]
    [InlineData("Mara: I promise to keep that between us.")]
    [InlineData("Player: I put a coin on the table.")]
    [InlineData("Smith: the price is forty gold, no bargaining.")]
    [InlineData("Guard: come back when you have the writ and I'll let you through.")]
    public void CommitmentsOfEveryShapeArePinned(string line)
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };

        interaction.AppendContext(line);

        Assert.Single(interaction.PinnedContext);
    }

    [Fact]
    public void TheSamePinnedCommitmentIsNotRecordedTwice()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };

        interaction.AppendContext("Pete: buy me a drink and I'll talk.");
        interaction.AppendContext("Pete: buy me a drink and I'll talk.");

        Assert.Single(interaction.PinnedContext);
    }

    [Fact]
    public void LeavingClearsTheWindowThePinsAndTheSummary()
    {
        var interaction = new InteractionState { Mode = InteractionMode.Conversation };
        for (var turn = 0; turn < 30; turn++)
            interaction.AppendContext($"Pete: I promise thing number {turn}.");

        interaction.Reset();

        Assert.Empty(interaction.Context);
        Assert.Empty(interaction.PinnedContext);
        Assert.Null(interaction.RunningSummary);
    }

    // ── End to end through the engine ──

    /// <summary>
    /// The whole point: an offer made early in a long conversation must still be an outstanding debt
    /// when the player walks away, so the NPC can settle it next time instead of re-offering.
    /// </summary>
    [Fact]
    public async Task APromiseMadeEarlyIsStillOwedAfterALongConversation()
    {
        var pete = new Npc
        {
            Id = "drunk_pete",
            Name = "Stumbling Pete",
            Personality = "Town drunk. Desperate for coin. Claims he saw the dungeon boss once.",
            Disposition = "neutral"
        };

        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern", Name = "The Rusted Flagon", Description = "A creaky tavern.",
            Npcs = [pete], Exits = new Dictionary<string, string> { ["north"] = "street" }
        });
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = "Meow Meow", Race = "Human", Class = "Warrior", Level = 5,
            CurrentRoomId = "tavern", Hp = 28, MaxHp = 28, Mp = 16, MaxMp = 19, Gold = 240,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10
        });

        var engine = CreateEngine(stateManager);

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to pete"));

        // The offer, made at the very start.
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId,
            "buy me a drink an' I'll tell you somethin' nobody else will"));

        // Bury it under far more turns than the verbatim window can hold.
        for (var turn = 0; turn < InteractionState.MaxContextEntries * 2; turn++)
            await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, $"tell me more about the weather, part {turn}"));

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "goodbye"));

        var room = await stateManager.GetPlayerRoomAsync(PlayerId, "tavern");
        var rememberedPete = room!.Npcs.First(n => n.Id == "drunk_pete");
        var memory = rememberedPete.DispositionState.Memory;

        Assert.NotEmpty(memory.OpenPromises);
        var block = memory.BuildPromptBlock("Meow Meow");
        Assert.Contains("You still owe them", block!, StringComparison.Ordinal);
    }

    private static GameEngine CreateEngine(IStateManager stateManager)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [10], Total = 10 });
        dice.Setup(d => d.RollSkillCheck(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string s, int m) => new DiceRoll { Expression = "1d20", Purpose = s, IndividualRolls = [10], Modifier = m, Total = 10 + m });

        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc n, InteractionState _, string input, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"\"{n.Name} responds.\"",
                InteractionUpdate = new InteractionUpdate
                {
                    Mode = InteractionMode.Conversation,
                    NpcDisposition = "neutral",
                    // Echo the player's line so the offer enters the conversation record.
                    Context = [$"Pete: {input}"]
                }
            });
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narration.");

        return new GameEngine(
            stateManager, dice.Object, narrator.Object,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);
    }
}
