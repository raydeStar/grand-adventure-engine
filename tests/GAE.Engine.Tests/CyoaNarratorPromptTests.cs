using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for C06 — CYOA narrator prompt templates: death narration, ending narration,
/// story-so-far context summarization, and integration with game engine.
/// </summary>
public class CyoaNarratorPromptTests
{
    // ── BuildCyoaStoryContext (static helper) ────────────────────

    [Fact]
    public void StoryContext_EmptyHistory_ReturnsBeginningSentence()
    {
        var history = Array.Empty<CyoaChoiceRecord>();

        var result = NarratorService.BuildCyoaStoryContext(history);

        Assert.Equal("This is the beginning of the story.", result);
    }

    [Fact]
    public void StoryContext_ShortHistory_ListsAllChoices()
    {
        var history = MakeHistory(5);

        var result = NarratorService.BuildCyoaStoryContext(history);

        // All 5 should be present individually
        for (var i = 0; i < 5; i++)
            Assert.Contains($"node-{i}", result);
    }

    [Fact]
    public void StoryContext_AtThreshold_StillListsAll()
    {
        var history = MakeHistory(8); // == summaryThreshold

        var result = NarratorService.BuildCyoaStoryContext(history);

        for (var i = 0; i < 8; i++)
            Assert.Contains($"node-{i}", result);

        // Should NOT contain summarized header
        Assert.DoesNotContain("EARLIER STORY", result);
    }

    [Fact]
    public void StoryContext_LongHistory_SummarizesOlderAndShowsRecent()
    {
        var history = MakeHistory(15);

        var result = NarratorService.BuildCyoaStoryContext(history);

        // Should contain summary section
        Assert.Contains("EARLIER STORY", result);
        Assert.Contains("10 choices summarized", result);

        // Recent 5 (indices 10-14) should be itemized
        Assert.Contains("RECENT CHOICES:", result);
        for (var i = 10; i < 15; i++)
            Assert.Contains($"node-{i}", result);
    }

    [Fact]
    public void StoryContext_LongHistory_IncludesKeyWaypoints()
    {
        var history = MakeHistory(15);

        var result = NarratorService.BuildCyoaStoryContext(history);

        // First choice and last of older section should appear as waypoints
        Assert.Contains("Choice at node-0", result);
        Assert.Contains("Key decisions:", result);
    }

    // ── Death narration integration ─────────────────────────────

    [Fact]
    public async Task Death_CallsNarratorDeathNarration()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create a save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "save-pre-death",
            NarrationText = "A quiet clearing.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Enter the cave" },
                new CyoaChoice { Id = "b", Text = "Walk away" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // Setup death narration mock
        narrator.Setup(s => s.GenerateCyoaDeathNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync("The cave collapses and TestHero is buried beneath stone.");

        SetupNextNode(narrator, "Enter the cave", new CyoaChoiceNode
        {
            NodeId = "cave-death",
            NarrationText = "Rocks fall from the ceiling.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "x" },
                new CyoaChoice { Id = "b", Text = "y" }
            ]
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // Should contain the narrator-generated death narration, not the raw node text
        Assert.Contains("buried beneath stone", result.Narration);
    }

    [Fact]
    public async Task Death_PassesHasCheckpointTrue_WhenSavePointsExist()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create a save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "save-a",
            NarrationText = "Safe.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Go on" },
                new CyoaChoice { Id = "b", Text = "Stay" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        bool? capturedHasCheckpoint = null;
        narrator.Setup(s => s.GenerateCyoaDeathNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<PlayerCharacter, string, bool, CancellationToken>((_, _, hasChk, _) => capturedHasCheckpoint = hasChk)
            .ReturnsAsync("You perish.");

        SetupNextNode(narrator, "Go on", new CyoaChoiceNode
        {
            NodeId = "death-a",
            NarrationText = "Trap.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "x" },
                new CyoaChoice { Id = "b", Text = "y" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.True(capturedHasCheckpoint);
    }

    [Fact]
    public async Task Death_PassesHasCheckpointFalse_WhenNoSavePoints()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        bool? capturedHasCheckpoint = null;
        narrator.Setup(s => s.GenerateCyoaDeathNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<PlayerCharacter, string, bool, CancellationToken>((_, _, hasChk, _) => capturedHasCheckpoint = hasChk)
            .ReturnsAsync("You perish utterly.");

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "instant-death",
            NarrationText = "A pit opens beneath you.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "x" },
                new CyoaChoice { Id = "b", Text = "y" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.False(capturedHasCheckpoint);
    }

    // ── Ending narration integration ────────────────────────────

    [Fact]
    public async Task Ending_CallsNarratorEndingNarration()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), "victory", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("TestHero stands triumphant atop the mountain of choices.");

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "victory-end",
            NarrationText = "The final door opens.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Contains("stands triumphant", result.Narration);
        Assert.Contains("The final door opens.", result.Narration);
        Assert.Contains("— The End —", result.Narration);
    }

    [Fact]
    public async Task Ending_PassesCorrectEndingType()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        string? capturedType = null;
        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PlayerCharacter, string, string, string, CancellationToken>((_, type, _, _, _) => capturedType = type)
            .ReturnsAsync("Epilogue.");

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "tragedy-end",
            NarrationText = "All is lost.",
            Ending = "tragedy"
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Equal("tragedy", capturedType);
    }

    [Fact]
    public async Task Ending_PassesSummaryToNarrator()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        string? capturedSummary = null;
        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PlayerCharacter, string, string, string, CancellationToken>((_, _, _, summary, _) => capturedSummary = summary)
            .ReturnsAsync("Epilogue text.");

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-summary",
            NarrationText = "Done.",
            Ending = "open"
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.NotNull(capturedSummary);
        Assert.Contains("Choices made:", capturedSummary);
    }

    [Fact]
    public async Task Ending_EpilogueAppearsBeforeTheEnd()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("EPILOGUE_MARKER");

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "order-test",
            NarrationText = "SCENE_MARKER",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var sceneIdx = result.Narration!.IndexOf("SCENE_MARKER");
        var epilogueIdx = result.Narration.IndexOf("EPILOGUE_MARKER");
        var endIdx = result.Narration.IndexOf("— The End —");

        Assert.True(sceneIdx < epilogueIdx, "Scene text should come before epilogue");
        Assert.True(epilogueIdx < endIdx, "Epilogue should come before The End marker");
    }

    // ── Fallback narration (NarratorService static helpers) ─────

    [Fact]
    public void DeathFallback_ContainsPlayerName()
    {
        // Access the fallback via the public method with a mock that throws
        var fallback = GetDeathFallback("Gandalf");
        Assert.Contains("Gandalf", fallback);
    }

    [Fact]
    public void EndingFallback_Victory_IsTriumphant()
    {
        var fallback = GetEndingFallback("Aria", "victory");
        Assert.Contains("Aria", fallback);
        Assert.Contains("triumphant", fallback);
    }

    [Fact]
    public void EndingFallback_Tragedy_IsSomber()
    {
        var fallback = GetEndingFallback("Kael", "tragedy");
        Assert.Contains("Kael", fallback);
        Assert.Contains("silence", fallback);
    }

    [Fact]
    public void EndingFallback_Cliffhanger_IsUnresolved()
    {
        var fallback = GetEndingFallback("Zara", "cliffhanger");
        Assert.Contains("Zara", fallback);
        Assert.Contains("ticking", fallback);
    }

    [Fact]
    public void EndingFallback_Open_IsReflective()
    {
        var fallback = GetEndingFallback("Rook", "open");
        Assert.Contains("Rook", fallback);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static List<CyoaChoiceRecord> MakeHistory(int count)
        => Enumerable.Range(0, count).Select(i => new CyoaChoiceRecord
        {
            Node = $"node-{i}",
            ChoiceText = $"Choice at node-{i}",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-count + i)
        }).ToList();

    private static string GetDeathFallback(string playerName)
    {
        // Use reflection to access the private static fallback method
        var method = typeof(NarratorService).GetMethod("BuildCyoaDeathFallback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [playerName])!;
    }

    private static string GetEndingFallback(string playerName, string endingType)
    {
        var method = typeof(NarratorService).GetMethod("BuildCyoaEndingFallback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, [playerName, endingType])!;
    }

    private static PlayerCharacter CreatePlayer() => new()
    {
        Id = "test-player",
        Name = "TestHero",
        CurrentRoomId = "start-room",
        GameMode = GameMode.FullRpg,
        Hp = 20, MaxHp = 20,
        Mp = 10, MaxMp = 10,
        Level = 1,
        Str = 10, Dex = 10, Con = 10, Int = 10, Wis = 10, Cha = 10,
        Gold = 50,
        ActiveWorldId = WorldDefaults.DefaultWorldId,
        HomeWorldId = WorldDefaults.DefaultWorldId
    };

    private static async Task<InMemoryStateManager> CreateStateAsync()
    {
        var stateManager = new InMemoryStateManager();

        var room = new Room { Id = "start-room", Name = "Start Room", Description = "A simple room." };
        await stateManager.SaveRoomAsync(room);

        var cyoaRoom = new Room { Id = "cyoa-room", Name = "CYOA Room", Description = "A story unfolds." };
        await stateManager.SaveRoomAsync(cyoaRoom);

        return stateManager;
    }

    private static async Task<(GameEngine Engine, InMemoryStateManager State, Mock<INarratorService> Narrator)> SetupCyoaSessionAsync()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        narrator.Setup(s => s.GenerateCyoaDeathNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The darkness takes you.");
        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("And so your story concludes.");
        narrator.Setup(s => s.GenerateCyoaNodeAsync(
                It.IsAny<PlayerCharacter>(), null,
                It.IsAny<IReadOnlyList<CyoaChoiceRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CyoaChoiceNode
            {
                NodeId = "opening",
                NarrationText = "You stand at the threshold of adventure.",
                Choices =
                [
                    new CyoaChoice { Id = "go-left", Text = "Take the left path" },
                    new CyoaChoice { Id = "go-right", Text = "Take the right path" }
                ]
            });

        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(state, dice.Object, narrator.Object, parser, new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var startAction = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaStart, RawInput = "cyoa start" };
        await engine.ProcessActionAsync(player.Id, startAction);

        return (engine, state, narrator);
    }

    private static void SetupNextNode(Mock<INarratorService> narrator, string choiceText, CyoaChoiceNode node)
    {
        narrator.Setup(s => s.GenerateCyoaNodeAsync(
                It.IsAny<PlayerCharacter>(),
                choiceText,
                It.IsAny<IReadOnlyList<CyoaChoiceRecord>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(node);
    }
}
