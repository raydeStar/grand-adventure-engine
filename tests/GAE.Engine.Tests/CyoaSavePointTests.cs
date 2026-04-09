using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for C03 — CYOA save points, death rewind, auto-save, and voluntary load.
/// </summary>
public class CyoaSavePointTests
{
    // ── Narrator-triggered save points ──────────────────────────

    [Fact]
    public async Task NarratorSavePoint_CreatesFullSnapshot()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "checkpoint-1",
            NarrationText = "You reach a safe campfire.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Rest by the fire" },
                new CyoaChoice { Id = "b", Text = "Press on" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Single(updated.CyoaState!.SavePoints);

        var save = updated.CyoaState.SavePoints[0];
        Assert.Equal("checkpoint-1", save.NodeId);
        Assert.Equal("You reach a safe campfire.", save.NarrationText);
        Assert.Equal(CyoaHealthLevel.Healthy, save.Health);
        Assert.Equal(2, save.Choices.Count);
        Assert.Equal(1, save.ChoiceCountAtSave); // 1 choice made so far
    }

    [Fact]
    public async Task NarratorSavePoint_CapturesInventorySnapshot()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // First node gives an item
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "found-torch",
            NarrationText = "You find a torch on the ground.",
            ItemsGained = ["Torch"],
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue deeper" },
                new CyoaChoice { Id = "b", Text = "Turn back" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        // Second node is a save point
        SetupNextNode(narrator, "Continue deeper", new CyoaChoiceNode
        {
            NodeId = "save-1",
            NarrationText = "A quiet rest stop.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Explore" },
                new CyoaChoice { Id = "b", Text = "Sleep" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" });

        var updated = (await state.GetPlayerAsync("test-player"))!;
        var save = updated.CyoaState!.SavePoints[0];
        Assert.Contains("Torch", save.Inventory);
    }

    // ── Auto-save ───────────────────────────────────────────────

    [Fact]
    public async Task AutoSave_TriggersEveryFiveChoices()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Choice 1: from opening, select "Take the left path"
        var choiceTexts = new[] { "Take the left path", "Continue", "Continue", "Continue", "Continue" };
        for (var i = 0; i < 5; i++)
        {
            SetupNextNode(narrator, choiceTexts[i], new CyoaChoiceNode
            {
                NodeId = $"node-{i + 1}",
                NarrationText = $"Scene {i + 1} unfolds.",
                Choices =
                [
                    new CyoaChoice { Id = "a", Text = "Continue" },
                    new CyoaChoice { Id = "b", Text = "Stop" }
                ]
            });

            await engine.ProcessActionAsync("test-player", new GameAction
            {
                PlayerId = "test-player",
                Type = ActionType.Unknown,
                RawInput = "1"
            });
        }

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Single(updated.CyoaState!.SavePoints); // Auto-save at choice 5
        Assert.Equal(5, updated.CyoaState.SavePoints[0].ChoiceCountAtSave);
    }

    [Fact]
    public async Task AutoSave_DoesNotTriggerOnNarratorSavePointAtSameCount()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Make 4 choices, then the 5th node is a narrator save point
        for (var i = 1; i <= 4; i++)
        {
            var choiceText = i == 1 ? "Take the left path" : "Continue";
            SetupNextNode(narrator, choiceText, new CyoaChoiceNode
            {
                NodeId = $"node-{i}",
                NarrationText = $"Scene {i}.",
                Choices =
                [
                    new CyoaChoice { Id = "a", Text = "Continue" },
                    new CyoaChoice { Id = "b", Text = "Stop" }
                ]
            });

            await engine.ProcessActionAsync("test-player", new GameAction
            {
                PlayerId = "test-player",
                Type = ActionType.Unknown,
                RawInput = "1"
            });
        }

        // 5th choice is a narrator save point — should NOT create a duplicate auto-save
        SetupNextNode(narrator, "Continue", new CyoaChoiceNode
        {
            NodeId = "narrator-save",
            NarrationText = "A safe haven.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Rest" },
                new CyoaChoice { Id = "b", Text = "Move on" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player",
            Type = ActionType.Unknown,
            RawInput = "1"
        });

        var updated = (await state.GetPlayerAsync("test-player"))!;
        // Only the narrator save — no auto-save duplicate
        Assert.Single(updated.CyoaState!.SavePoints);
        Assert.Equal("narrator-save", updated.CyoaState.SavePoints[0].NodeId);
    }

    // ── Death rewind ────────────────────────────────────────────

    [Fact]
    public async Task Death_WithSavePoint_RewindsToLastSave()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Step 1: Save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "checkpoint",
            NarrationText = "A campfire crackles.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Enter the dark passage" },
                new CyoaChoice { Id = "b", Text = "Stay by the fire" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Step 2: Death node
        SetupNextNode(narrator, "Enter the dark passage", new CyoaChoiceNode
        {
            NodeId = "death-trap",
            NarrationText = "The ceiling collapses on you.",
            HealthChange = "dead",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "n/a" }
            ]
        });

        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Should show death narration + rewind
        Assert.Contains("ceiling collapses", result.Narration);
        Assert.Contains("Death claims you", result.Narration);
        Assert.Contains("campfire crackles", result.Narration); // Save point narration re-shown

        // Player should be restored to save point state
        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal("checkpoint", updated.CyoaState!.CurrentNode);
        Assert.Equal(CyoaHealthLevel.Healthy, updated.CyoaState.Health);
        Assert.Equal(2, updated.CyoaState.CurrentChoices.Count);
    }

    [Fact]
    public async Task Death_WithSavePoint_RestoresInventory()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Give player an item first
        var player = (await state.GetPlayerAsync("test-player"))!;
        player.CyoaState!.Inventory.Add("Magic Ring");
        await state.SavePlayerAsync(player);

        // Save point (captures inventory with Magic Ring)
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "safe-spot",
            NarrationText = "A safe clearing.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Go forward" },
                new CyoaChoice { Id = "b", Text = "Wait" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Lose the item, then die
        SetupNextNode(narrator, "Go forward", new CyoaChoiceNode
        {
            NodeId = "death",
            NarrationText = "A thief takes your ring and pushes you off a cliff.",
            HealthChange = "dead",
            ItemsLost = ["Magic Ring"],
            Choices = [new CyoaChoice { Id = "a", Text = "n/a" }]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Contains("Magic Ring", updated.CyoaState!.Inventory); // Restored!
    }

    [Fact]
    public async Task Death_WithSavePoint_TruncatesChoiceHistory()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "cp",
            NarrationText = "Checkpoint.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Danger" },
                new CyoaChoice { Id = "b", Text = "Safety" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Die
        SetupNextNode(narrator, "Danger", new CyoaChoiceNode
        {
            NodeId = "death",
            NarrationText = "You fall.",
            HealthChange = "dead",
            Choices = [new CyoaChoice { Id = "a", Text = "n/a" }]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        var updated = (await state.GetPlayerAsync("test-player"))!;
        // Only 1 choice in history (the one BEFORE the save point, i.e. the initial choice)
        Assert.Single(updated.CyoaState!.ChoiceHistory);
    }

    [Fact]
    public async Task Death_WithoutSavePoint_GameOver()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Immediate death, no save points
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "sudden-death",
            NarrationText = "A boulder crushes you.",
            HealthChange = "dead",
            Choices = [new CyoaChoice { Id = "a", Text = "n/a" }]
        });

        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        Assert.Contains("cyoa end", result.Narration);
        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Empty(updated.CyoaState!.CurrentChoices); // No choices — game over
    }

    // ── !save command ───────────────────────────────────────────

    [Fact]
    public async Task SaveList_ShowsSavePoints()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create a save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "cp",
            NarrationText = "A campfire in the cave.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Rest" },
                new CyoaChoice { Id = "b", Text = "Go on" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Use !save to list
        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaSaveList, RawInput = "!save"
        });

        Assert.True(result.Success);
        Assert.Contains("Save Points", result.Narration);
        Assert.Contains("campfire in the cave", result.Narration);
    }

    [Fact]
    public async Task SaveList_EmptyShowsMessage()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaSaveList, RawInput = "!save"
        });

        Assert.True(result.Success);
        Assert.Contains("No save points yet", result.Narration);
    }

    // ── !load command ───────────────────────────────────────────

    [Fact]
    public async Task Load_RewindToSpecificSave()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create save point 1
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "save-1",
            NarrationText = "First clearing.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Go deeper" },
                new CyoaChoice { Id = "b", Text = "Turn back" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Create save point 2
        SetupNextNode(narrator, "Go deeper", new CyoaChoiceNode
        {
            NodeId = "save-2",
            NarrationText = "Second clearing.",
            IsSavePoint = true,
            HealthChange = "worse",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Enter dungeon" },
                new CyoaChoice { Id = "b", Text = "Camp here" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Load save point 1 (not the latest)
        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaLoad, RawInput = "!load 1"
        });

        Assert.True(result.Success);
        Assert.Contains("First clearing", result.Narration);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal("save-1", updated.CyoaState!.CurrentNode);
        Assert.Equal(CyoaHealthLevel.Healthy, updated.CyoaState.Health); // Before the "worse" hit
        Assert.Single(updated.CyoaState.ChoiceHistory); // Only the first choice
        // Save point 2 should be discarded (after loaded save)
        Assert.Single(updated.CyoaState.SavePoints);
    }

    [Fact]
    public async Task Load_NoNumber_RewindsToLatest()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "latest-save",
            NarrationText = "The latest save.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue" },
                new CyoaChoice { Id = "b", Text = "Wait" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Advance past the save
        SetupNextNode(narrator, "Continue", new CyoaChoiceNode
        {
            NodeId = "beyond-save",
            NarrationText = "Far from safety.",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Onward" },
                new CyoaChoice { Id = "b", Text = "Back" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // !load with no number → loads most recent
        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaLoad, RawInput = "!load"
        });

        Assert.True(result.Success);
        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal("latest-save", updated.CyoaState!.CurrentNode);
    }

    [Fact]
    public async Task Load_InvalidNumber_Fails()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create 1 save point
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "cp",
            NarrationText = "Only save.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Go" },
                new CyoaChoice { Id = "b", Text = "Stay" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaLoad, RawInput = "!load 5"
        });

        Assert.False(result.Success);
        Assert.Contains("Invalid save point", result.Narration);
    }

    [Fact]
    public async Task Load_NoSaves_Fails()
    {
        var (engine, _, _) = await SetupCyoaSessionAsync();

        var result = await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaLoad, RawInput = "!load"
        });

        Assert.False(result.Success);
        Assert.Contains("No save points available", result.Narration);
    }

    [Fact]
    public async Task Load_DiscardsProgressAfterSave()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Save point with no items
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "early-save",
            NarrationText = "Starting point.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Venture forth" },
                new CyoaChoice { Id = "b", Text = "Stay put" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Gain an item after the save
        SetupNextNode(narrator, "Venture forth", new CyoaChoiceNode
        {
            NodeId = "post-save",
            NarrationText = "You find a sword.",
            ItemsGained = ["Rusty Sword"],
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue" },
                new CyoaChoice { Id = "b", Text = "Drop it" }
            ]
        });

        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
        });

        // Verify item was gained
        var beforeLoad = (await state.GetPlayerAsync("test-player"))!;
        Assert.Contains("Rusty Sword", beforeLoad.CyoaState!.Inventory);

        // Load back to the save point
        await engine.ProcessActionAsync("test-player", new GameAction
        {
            PlayerId = "test-player", Type = ActionType.CyoaLoad, RawInput = "!load 1"
        });

        var afterLoad = (await state.GetPlayerAsync("test-player"))!;
        Assert.DoesNotContain("Rusty Sword", afterLoad.CyoaState!.Inventory);
        Assert.Equal("early-save", afterLoad.CyoaState.CurrentNode);
    }

    // ── Save cap ────────────────────────────────────────────────

    [Fact]
    public async Task SavePoints_CappedAtTen()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Create 11 save points
        var choiceText = "Take the left path";
        for (var i = 1; i <= 11; i++)
        {
            SetupNextNode(narrator, choiceText, new CyoaChoiceNode
            {
                NodeId = $"save-{i}",
                NarrationText = $"Save point {i}.",
                IsSavePoint = true,
                Choices =
                [
                    new CyoaChoice { Id = "a", Text = "Next" },
                    new CyoaChoice { Id = "b", Text = "Stop" }
                ]
            });

            await engine.ProcessActionAsync("test-player", new GameAction
            {
                PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1"
            });

            choiceText = "Next";
        }

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(10, updated.CyoaState!.SavePoints.Count);
        // Oldest (save-1) should have been evicted
        Assert.Equal("save-2", updated.CyoaState.SavePoints[0].NodeId);
        Assert.Equal("save-11", updated.CyoaState.SavePoints[^1].NodeId);
    }

    // ── CommandParser ───────────────────────────────────────────

    [Theory]
    [InlineData("!save")]
    [InlineData("cyoa saves")]
    [InlineData("cyoa save")]
    [InlineData("book saves")]
    public void CommandParser_RecognizesSaveList(string input)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", input);
        Assert.Equal(ActionType.CyoaSaveList, action.Type);
    }

    [Theory]
    [InlineData("!load")]
    [InlineData("!load 2")]
    [InlineData("cyoa load")]
    [InlineData("cyoa load 3")]
    [InlineData("book load 1")]
    public void CommandParser_RecognizesLoad(string input)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", input);
        Assert.Equal(ActionType.CyoaLoad, action.Type);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static PlayerCharacter CreatePlayer() => new()
    {
        Id = "test-player",
        Name = "Tester",
        CurrentRoomId = "spawn",
        Hp = 20, MaxHp = 20,
        Mp = 10, MaxMp = 10,
        Level = 1, Xp = 0,
        Str = 10, Dex = 10, Con = 10, Int = 10, Wis = 10, Cha = 10,
        Gold = 50,
        ActiveWorldId = WorldDefaults.DefaultWorldId,
        HomeWorldId = WorldDefaults.DefaultWorldId
    };

    private static async Task<InMemoryStateManager> CreateStateAsync()
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "spawn",
            Name = "The Crossroads Inn",
            Description = "A weathered inn at the junction of three roads.",
            Exits = new Dictionary<string, string> { ["north"] = "forest" }
        });
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
            .Returns<PlayerCharacter, string, bool, CancellationToken>((_, scene, _, _) => Task.FromResult(scene));
        narrator.Setup(s => s.GenerateCyoaEndingNarrationAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("And so the story ends.");
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
