using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for C02 — CYOA choice tree and branch generation.
/// Covers choice selection by number and text, narrator chaining,
/// item-gated choices, health changes, death, and save points.
/// </summary>
public class CyoaChoiceTreeTests
{
    // ── Choice by number ────────────────────────────────────────

    [Fact]
    public async Task ChoiceByNumber_SelectsCorrectChoice()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();
        var player = (await state.GetPlayerAsync("test-player"))!;

        // Set up the narrator to return a second node when "Take the left path" is chosen
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "dark-cave",
            NarrationText = "The left path leads into a dark cave.",
            Choices =
            [
                new CyoaChoice { Id = "enter-cave", Text = "Enter the cave" },
                new CyoaChoice { Id = "wait-outside", Text = "Wait outside" }
            ]
        });

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Unknown, RawInput = "1" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.True(result.Success);
        Assert.Contains("dark cave", result.Narration);
        Assert.Contains("Enter the cave", result.Narration);

        var updated = (await state.GetPlayerAsync(player.Id))!;
        Assert.Equal("dark-cave", updated.CyoaState!.CurrentNode);
        Assert.Equal(2, updated.CyoaState.CurrentChoices.Count);
        Assert.Single(updated.CyoaState.ChoiceHistory);
        Assert.Equal("Take the left path", updated.CyoaState.ChoiceHistory[0].ChoiceText);
    }

    [Fact]
    public async Task ChoiceByNumber_SecondChoice()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the right path", new CyoaChoiceNode
        {
            NodeId = "sunny-meadow",
            NarrationText = "The right path opens into a sunny meadow.",
            Choices =
            [
                new CyoaChoice { Id = "pick-flowers", Text = "Pick wildflowers" },
                new CyoaChoice { Id = "keep-walking", Text = "Keep walking" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "2" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
        Assert.Contains("sunny meadow", result.Narration);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal("Take the right path", updated.CyoaState!.ChoiceHistory[0].ChoiceText);
    }

    // ── Choice by text match ────────────────────────────────────

    [Fact]
    public async Task ChoiceByText_MatchesPartialInput()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "node-2",
            NarrationText = "You head left.",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue" },
                new CyoaChoice { Id = "b", Text = "Stop" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "left path" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
        Assert.Contains("head left", result.Narration);
    }

    // ── Invalid choice ──────────────────────────────────────────

    [Fact]
    public async Task InvalidNumber_RepresentsChoices()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "5" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.False(result.Success);
        Assert.Contains("Invalid choice", result.MechanicalSummary);
        Assert.Contains("Take the left path", result.Narration);
    }

    [Fact]
    public async Task InvalidText_RepresentsChoices()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "fly to the moon" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.False(result.Success);
        Assert.Contains("Take the right path", result.Narration);
    }

    // ── Multi-node chain ────────────────────────────────────────

    [Fact]
    public async Task ThreeNodeChain_TracksFullHistory()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Node 2
        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "node-2",
            NarrationText = "A fork in the road.",
            Choices =
            [
                new CyoaChoice { Id = "climb", Text = "Climb the wall" },
                new CyoaChoice { Id = "swim", Text = "Swim the river" }
            ]
        });

        var action1 = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action1);

        // Node 3
        SetupNextNode(narrator, "Climb the wall", new CyoaChoiceNode
        {
            NodeId = "node-3",
            NarrationText = "You reach the top.",
            Choices =
            [
                new CyoaChoice { Id = "jump", Text = "Jump down" },
                new CyoaChoice { Id = "rest", Text = "Rest at the top" }
            ]
        });

        var action2 = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action2);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal("node-3", updated.CyoaState!.CurrentNode);
        Assert.Equal(2, updated.CyoaState.ChoiceHistory.Count);
        Assert.Equal("Take the left path", updated.CyoaState.ChoiceHistory[0].ChoiceText);
        Assert.Equal("Climb the wall", updated.CyoaState.ChoiceHistory[1].ChoiceText);
    }

    // ── Health changes ──────────────────────────────────────────

    [Fact]
    public async Task HealthChange_AppliedFromNode()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "trap-room",
            NarrationText = "A blade swings from the ceiling!",
            HealthChange = "worse",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Dodge" },
                new CyoaChoice { Id = "b", Text = "Block" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(CyoaHealthLevel.Hurt, updated.CyoaState!.Health); // Healthy → Hurt
    }

    [Fact]
    public async Task Death_ClearsChoicesAndShowsMessage()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Make player already critical
        var player = (await state.GetPlayerAsync("test-player"))!;
        player.CyoaState!.Health = CyoaHealthLevel.Critical;
        await state.SavePlayerAsync(player);

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "death-node",
            NarrationText = "The floor gives way beneath you.",
            HealthChange = "worse",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Scream" },
                new CyoaChoice { Id = "b", Text = "Flap your arms" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
        Assert.Contains("💀", result.Narration);
        Assert.Contains("adventure has ended in death", result.Narration);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(CyoaHealthLevel.Dead, updated.CyoaState!.Health);
        Assert.Empty(updated.CyoaState.CurrentChoices);
    }

    // ── Item changes ────────────────────────────────────────────

    [Fact]
    public async Task ItemsGained_AddedToInventory()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "loot-room",
            NarrationText = "You find a chest.",
            ItemsGained = ["Rusty Key", "Torch"],
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue" },
                new CyoaChoice { Id = "b", Text = "Search more" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Contains("Rusty Key", updated.CyoaState!.Inventory);
        Assert.Contains("Torch", updated.CyoaState.Inventory);
    }

    [Fact]
    public async Task ItemsLost_RemovedFromInventory()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Give the player an item first
        var player = (await state.GetPlayerAsync("test-player"))!;
        player.CyoaState!.Inventory.Add("Old Map");
        await state.SavePlayerAsync(player);

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "trade-room",
            NarrationText = "A merchant takes your map.",
            ItemsLost = ["Old Map"],
            ItemsGained = ["Compass"],
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Thank them" },
                new CyoaChoice { Id = "b", Text = "Demand it back" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.DoesNotContain("Old Map", updated.CyoaState!.Inventory);
        Assert.Contains("Compass", updated.CyoaState.Inventory);
    }

    // ── Item-gated choices ──────────────────────────────────────

    [Fact]
    public async Task RequiredItems_FiltersUnavailableChoices()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "locked-door",
            NarrationText = "A locked door blocks your path.",
            Choices =
            [
                new CyoaChoice { Id = "pick-lock", Text = "Pick the lock", RequiredItems = ["Lockpick"] },
                new CyoaChoice { Id = "use-key", Text = "Use the rusty key", RequiredItems = ["Rusty Key"] },
                new CyoaChoice { Id = "kick-door", Text = "Kick the door down" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        // Player has no items, so only the ungated choice should be available
        Assert.Single(updated.CyoaState!.CurrentChoices);
        Assert.Equal("Kick the door down", updated.CyoaState.CurrentChoices[0].Text);
    }

    [Fact]
    public async Task RequiredItems_ShowsChoiceWhenPlayerHasItem()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Give the player a Rusty Key
        var player = (await state.GetPlayerAsync("test-player"))!;
        player.CyoaState!.Inventory.Add("Rusty Key");
        await state.SavePlayerAsync(player);

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "locked-door",
            NarrationText = "A locked door blocks your path.",
            Choices =
            [
                new CyoaChoice { Id = "use-key", Text = "Use the rusty key", RequiredItems = ["Rusty Key"] },
                new CyoaChoice { Id = "kick-door", Text = "Kick the door down" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Equal(2, updated.CyoaState!.CurrentChoices.Count);
    }

    // ── Save points ─────────────────────────────────────────────

    [Fact]
    public async Task SavePoint_AddedToList()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "checkpoint-1",
            NarrationText = "You reach a safe haven.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Rest" },
                new CyoaChoice { Id = "b", Text = "Continue" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Contains(updated.CyoaState!.SavePoints, s => s.NodeId == "checkpoint-1");
    }

    [Fact]
    public async Task SavePoint_NoDuplicates()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        // Pre-add the save point
        var player = (await state.GetPlayerAsync("test-player"))!;
        player.CyoaState!.SavePoints.Add(new CyoaSaveSnapshot
        {
            NodeId = "checkpoint-1",
            NarrationText = "Previous save.",
            Health = CyoaHealthLevel.Healthy,
            Choices = [new CyoaChoice { Id = "a", Text = "Rest" }, new CyoaChoice { Id = "b", Text = "Continue" }]
        });
        await state.SavePlayerAsync(player);

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "checkpoint-1",
            NarrationText = "You're back at the safe haven.",
            IsSavePoint = true,
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Rest" },
                new CyoaChoice { Id = "b", Text = "Continue" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Single(updated.CyoaState!.SavePoints);
    }

    // ── Passthrough commands ────────────────────────────────────

    [Fact]
    public async Task StatsCommand_PassesThroughDuringCyoa()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Stats, RawInput = "stats" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
        // CYOA stats show health description (from C04 ProcessStats branch)
        Assert.Contains("Health", result.MechanicalSummary);
    }

    [Fact]
    public async Task InventoryCommand_PassesThroughDuringCyoa()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Inventory, RawInput = "inventory" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task CyoaEnd_StillWorksDuringCyoaTurn()
    {
        var (engine, state, _) = await SetupCyoaSessionAsync();

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.CyoaEnd, RawInput = "cyoa end" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.True(result.Success);
        Assert.Contains("Adventure ended", result.MechanicalSummary);

        var updated = (await state.GetPlayerAsync("test-player"))!;
        Assert.Null(updated.CyoaState);
        Assert.Equal(GameMode.FullRpg, updated.GameMode);
    }

    // ── Narration format ────────────────────────────────────────

    [Fact]
    public async Task NarrationFormat_IncludesNumberedChoices()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "node-2",
            NarrationText = "The scene continues.",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Option Alpha" },
                new CyoaChoice { Id = "b", Text = "Option Beta" },
                new CyoaChoice { Id = "c", Text = "Option Gamma" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        var result = await engine.ProcessActionAsync("test-player", action);

        Assert.Contains("**1.** Option Alpha", result.Narration);
        Assert.Contains("**2.** Option Beta", result.Narration);
        Assert.Contains("**3.** Option Gamma", result.Narration);
    }

    // ── Story logging ───────────────────────────────────────────

    [Fact]
    public async Task CyoaTurn_PersistsStoryEntry()
    {
        var (engine, state, narrator) = await SetupCyoaSessionAsync();

        SetupNextNode(narrator, "Take the left path", new CyoaChoiceNode
        {
            NodeId = "node-2",
            NarrationText = "You press on.",
            Choices =
            [
                new CyoaChoice { Id = "a", Text = "Continue" },
                new CyoaChoice { Id = "b", Text = "Stop" }
            ]
        });

        var action = new GameAction { PlayerId = "test-player", Type = ActionType.Unknown, RawInput = "1" };
        await engine.ProcessActionAsync("test-player", action);

        var stories = await state.GetRecentStoryForRoomAsync("spawn", WorldDefaults.DefaultWorldId, 10);
        // Should have at least the CYOA start entry + the turn entry
        Assert.True(stories.Count >= 2);
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

    /// <summary>
    /// Creates a fully initialized CYOA session: player starts, narrator generates the opening node,
    /// and the player is ready to make their first choice.
    /// </summary>
    private static async Task<(GameEngine Engine, InMemoryStateManager State, Mock<INarratorService> Narrator)> SetupCyoaSessionAsync()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>();
        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated.");
        // Opening node
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

        // Start the CYOA session
        var startAction = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaStart, RawInput = "cyoa start" };
        await engine.ProcessActionAsync(player.Id, startAction);

        return (engine, state, narrator);
    }

    /// <summary>
    /// Sets up the narrator mock to return a specific node when a choice text is selected.
    /// </summary>
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
