using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for C05 — CYOA endgame: narrator ending signals, forced endings at max nodes,
/// adventure summary, death counter, and session cleanup.
/// </summary>
public class CyoaEndgameTests
{
    // ── Narrator ending signal ──────────────────────────────────

    [Fact]
    public async Task NarratorEndingSignal_ConcludesSession()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "victory-node",
            NarrationText = "You emerge triumphant into the sunlight.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(GameMode.FullRpg, player.GameMode);
        Assert.Null(player.CyoaState);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task NarratorEndingSignal_ShowsEndingNarration()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "tragedy-node",
            NarrationText = "The darkness swallows everything.",
            Ending = "tragedy"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Contains("The darkness swallows everything.", result.Narration);
        Assert.Contains("— The End —", result.Narration);
    }

    [Fact]
    public async Task NarratorEndingSignal_IncludesAdventureSummary()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-1",
            NarrationText = "The story concludes.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Contains("Adventure Summary", result.Narration);
        Assert.Contains("Choices made:", result.Narration);
        Assert.Contains("Victory", result.Narration);
    }

    [Theory]
    [InlineData("victory", "Victory")]
    [InlineData("tragedy", "Tragedy")]
    [InlineData("cliffhanger", "Cliffhanger")]
    [InlineData("open", "Open Ending")]
    public async Task EndingType_AppearInSummary(string endingType, string expectedLabel)
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-typed",
            NarrationText = "Fin.",
            Ending = endingType
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Contains(expectedLabel, result.Narration);
    }

    // ── Forced ending at max nodes ──────────────────────────────

    [Fact]
    public async Task ForcedEnding_TriggersAtMaxNodes()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Burn through 30 choices to hit CyoaMaxNodes
        for (var i = 0; i < 30; i++)
        {
            var choiceText = i == 0 ? "Take the left path" : "Continue";
            SetupNextNode(narrator, choiceText, new CyoaChoiceNode
            {
                NodeId = $"node-{i + 1}",
                NarrationText = $"Scene {i + 1}.",
                Choices =
                [
                    new CyoaChoice { Id = "a", Text = "Continue" },
                    new CyoaChoice { Id = "b", Text = "Stop" }
                ]
            });

            await engine.ProcessActionAsync("test-player",
                new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });
        }

        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(GameMode.FullRpg, player.GameMode);
        Assert.Null(player.CyoaState);
    }

    [Fact]
    public async Task ForcedEnding_HasOpenEndingType()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        ActionResult? lastResult = null;
        for (var i = 0; i < 30; i++)
        {
            var choiceText = i == 0 ? "Take the left path" : "Continue";
            SetupNextNode(narrator, choiceText, new CyoaChoiceNode
            {
                NodeId = $"node-{i + 1}",
                NarrationText = $"Scene {i + 1}.",
                Choices =
                [
                    new CyoaChoice { Id = "a", Text = "Continue" },
                    new CyoaChoice { Id = "b", Text = "Stop" }
                ]
            });

            lastResult = await engine.ProcessActionAsync("test-player",
                new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });
        }

        Assert.Contains("Open Ending", lastResult!.Narration);
        Assert.Contains("— The End —", lastResult.Narration);
    }

    // ── Session cleanup ─────────────────────────────────────────

    [Fact]
    public async Task Ending_RestoresPlayerToOriginalRoom()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();
        var beforePlayer = (await state.GetPlayerAsync("test-player"))!;
        var originalRoom = "start-room"; // PreviousRoomId from the session setup

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-room-restore",
            NarrationText = "You wake up.",
            Ending = "open"
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(originalRoom, player.CurrentRoomId);
    }

    [Fact]
    public async Task Ending_ResetsInteractionMode()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-mode",
            NarrationText = "Done.",
            Ending = "victory"
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(InteractionMode.Explore, player.Interaction.Mode);
    }

    [Fact]
    public async Task Ending_ReportsGameModeStateChange()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "end-sc",
            NarrationText = "Fin.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var sc = Assert.Single(result.StateChanges);
        Assert.Equal("GameMode", sc.Property);
        Assert.Equal(nameof(GameMode.ChooseYourOwnAdventure), sc.OldValue);
        Assert.Equal(nameof(GameMode.FullRpg), sc.NewValue);
    }

    // ── Death counter ───────────────────────────────────────────

    [Fact]
    public async Task DeathRewind_IncrementsDeathCount()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create a save point via narrator
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "save-before-death",
            NarrationText = "A safe haven.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Enter the trap" },
                new CyoaChoice { Id = "b", Text = "Avoid" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // Die from lethal damage
        SetupNextNode(narrator, "Enter the trap", new CyoaChoiceNode
        {
            NodeId = "death-node",
            NarrationText = "The trap kills you.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "ghost" },
                new CyoaChoice { Id = "b", Text = "ghost2" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(1, player.CyoaState!.DeathCount);
    }

    [Fact]
    public async Task DeathCount_AppearsInSummaryWhenNonZero()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Save → die → rewind → end
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "save-x",
            NarrationText = "Safe.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Enter danger" },
                new CyoaChoice { Id = "b", Text = "Avoid" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // Die
        SetupNextNode(narrator, "Enter danger", new CyoaChoiceNode
        {
            NodeId = "die-1",
            NarrationText = "Death.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "x" },
                new CyoaChoice { Id = "b", Text = "y" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // After rewind, end the story
        SetupNextNode(narrator, "Avoid", new CyoaChoiceNode
        {
            NodeId = "final-end",
            NarrationText = "You escape.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "2" });

        Assert.Contains("Deaths:", result.Narration);
    }

    [Fact]
    public async Task DeathCount_OmittedFromSummaryWhenZero()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "clean-end",
            NarrationText = "Victory without dying.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.DoesNotContain("Deaths:", result.Narration);
    }

    // ── Items in summary ────────────────────────────────────────

    [Fact]
    public async Task Summary_IncludesCollectedItems()
    {
        var (engine, _, narrator) = await SetupCyoaSessionAsync();

        // Gain an item then end
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "loot-node",
            NarrationText = "Found a gem.",
            ItemsGained = ["Ruby Gem"],
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Finish" },
                new CyoaChoice { Id = "b", Text = "Wait" }
            ]
        });

        await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        SetupNextNode(narrator, "Finish", new CyoaChoiceNode
        {
            NodeId = "end-with-items",
            NarrationText = "Done.",
            Ending = "victory"
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.Contains("Ruby Gem", result.Narration);
        Assert.Contains("Items collected:", result.Narration);
    }

    // ── Narrator ending node with zero choices is valid ─────────

    [Fact]
    public async Task EndingNode_WithZeroChoices_IsValid()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "zero-choice-end",
            NarrationText = "It is over.",
            Ending = "cliffhanger",
            Choices = [] // no choices — valid because ending is set
        });

        var result = await engine.ProcessActionAsync("test-player",
            new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        Assert.True(result.Success);
        var player = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(GameMode.FullRpg, player.GameMode);
    }

    // ── Helpers ─────────────────────────────────────────────────

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
