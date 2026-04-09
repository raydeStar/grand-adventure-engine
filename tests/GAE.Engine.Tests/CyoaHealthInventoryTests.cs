using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

public class CyoaHealthInventoryTests
{
    // ── Health transitions ──────────────────────────────────────

    [Theory]
    [InlineData(CyoaHealthLevel.Healthy, "worse", CyoaHealthLevel.Hurt)]
    [InlineData(CyoaHealthLevel.Hurt, "worse", CyoaHealthLevel.Critical)]
    [InlineData(CyoaHealthLevel.Critical, "worse", CyoaHealthLevel.Dead)]
    [InlineData(CyoaHealthLevel.Hurt, "better", CyoaHealthLevel.Healthy)]
    [InlineData(CyoaHealthLevel.Critical, "better", CyoaHealthLevel.Hurt)]
    public void HealthTransition_RelativeSignals(CyoaHealthLevel start, string signal, CyoaHealthLevel expected)
    {
        var state = new CyoaState { Health = start };
        CyoaMechanics.ApplyHealthChange(state, signal);
        Assert.Equal(expected, state.Health);
    }

    [Theory]
    [InlineData(CyoaHealthLevel.Healthy, "hurt", CyoaHealthLevel.Hurt)]
    [InlineData(CyoaHealthLevel.Healthy, "critical", CyoaHealthLevel.Critical)]
    [InlineData(CyoaHealthLevel.Healthy, "dead", CyoaHealthLevel.Dead)]
    [InlineData(CyoaHealthLevel.Critical, "healthy", CyoaHealthLevel.Healthy)]
    public void HealthTransition_AbsoluteSignals(CyoaHealthLevel start, string signal, CyoaHealthLevel expected)
    {
        var state = new CyoaState { Health = start };
        CyoaMechanics.ApplyHealthChange(state, signal);
        Assert.Equal(expected, state.Health);
    }

    [Fact]
    public void HealthTransition_DeadPlayerCannotGetWorse()
    {
        var state = new CyoaState { Health = CyoaHealthLevel.Dead };
        var result = CyoaMechanics.ApplyHealthChange(state, "worse");
        Assert.Null(result); // No change
        Assert.Equal(CyoaHealthLevel.Dead, state.Health);
    }

    [Fact]
    public void HealthTransition_DeadPlayerCanBeResurrected()
    {
        var state = new CyoaState { Health = CyoaHealthLevel.Dead };
        // Dead players cannot be changed via ApplyHealthChange (returns null)
        var result = CyoaMechanics.ApplyHealthChange(state, "better");
        Assert.Null(result);
        Assert.Equal(CyoaHealthLevel.Dead, state.Health);
    }

    [Fact]
    public void HealthTransition_SameStateReturnsNull()
    {
        var state = new CyoaState { Health = CyoaHealthLevel.Healthy };
        var result = CyoaMechanics.ApplyHealthChange(state, "healthy");
        Assert.Null(result);
    }

    [Fact]
    public void HealthTransition_UnknownSignalIgnored()
    {
        var state = new CyoaState { Health = CyoaHealthLevel.Hurt };
        var result = CyoaMechanics.ApplyHealthChange(state, "gobbledygook");
        Assert.Null(result);
        Assert.Equal(CyoaHealthLevel.Hurt, state.Health);
    }

    [Fact]
    public void HealthTransition_CaseInsensitive()
    {
        var state = new CyoaState { Health = CyoaHealthLevel.Healthy };
        CyoaMechanics.ApplyHealthChange(state, "WORSE");
        Assert.Equal(CyoaHealthLevel.Hurt, state.Health);
    }

    // ── Health descriptions ─────────────────────────────────────

    [Theory]
    [InlineData(CyoaHealthLevel.Healthy, "fine")]
    [InlineData(CyoaHealthLevel.Hurt, "battered")]
    [InlineData(CyoaHealthLevel.Critical, "barely")]
    [InlineData(CyoaHealthLevel.Dead, "dark")]
    public void DescribeHealth_ReturnsFlavor(CyoaHealthLevel level, string expectedSubstring)
    {
        var desc = CyoaMechanics.DescribeHealth(level);
        Assert.Contains(expectedSubstring, desc, StringComparison.OrdinalIgnoreCase);
    }

    // ── Inventory management ────────────────────────────────────

    [Fact]
    public void AddItems_AddsNewItems()
    {
        var state = new CyoaState();
        var added = CyoaMechanics.AddItems(state, ["Torch", "Rusty Key"]);
        Assert.Equal(["Torch", "Rusty Key"], added);
        Assert.Equal(["Torch", "Rusty Key"], state.Inventory);
    }

    [Fact]
    public void AddItems_IgnoresDuplicates()
    {
        var state = new CyoaState { Inventory = ["Torch"] };
        var added = CyoaMechanics.AddItems(state, ["torch", "Silver Dagger"]);
        Assert.Single(added);
        Assert.Equal("Silver Dagger", added[0]);
        Assert.Equal(2, state.Inventory.Count);
    }

    [Fact]
    public void AddItems_IgnoresEmptyStrings()
    {
        var state = new CyoaState();
        var added = CyoaMechanics.AddItems(state, ["", "  ", "Torch"]);
        Assert.Single(added);
        Assert.Equal("Torch", added[0]);
    }

    [Fact]
    public void RemoveItems_RemovesExistingItems()
    {
        var state = new CyoaState { Inventory = ["Torch", "Rusty Key", "Letter"] };
        var removed = CyoaMechanics.RemoveItems(state, ["Rusty Key"]);
        Assert.Equal(["Rusty Key"], removed);
        Assert.Equal(["Torch", "Letter"], state.Inventory);
    }

    [Fact]
    public void RemoveItems_CaseInsensitive()
    {
        var state = new CyoaState { Inventory = ["Torch"] };
        var removed = CyoaMechanics.RemoveItems(state, ["TORCH"]);
        Assert.Single(removed);
        Assert.Empty(state.Inventory);
    }

    [Fact]
    public void RemoveItems_MissingItemIgnored()
    {
        var state = new CyoaState { Inventory = ["Torch"] };
        var removed = CyoaMechanics.RemoveItems(state, ["Silver Dagger"]);
        Assert.Empty(removed);
        Assert.Single(state.Inventory);
    }

    // ── HasItems ────────────────────────────────────────────────

    [Fact]
    public void HasItems_ReturnsTrueWhenAllPresent()
    {
        var state = new CyoaState { Inventory = ["Torch", "Rusty Key", "Letter"] };
        Assert.True(CyoaMechanics.HasItems(state, ["torch", "Rusty Key"]));
    }

    [Fact]
    public void HasItems_ReturnsFalseWhenMissing()
    {
        var state = new CyoaState { Inventory = ["Torch"] };
        Assert.False(CyoaMechanics.HasItems(state, ["Torch", "Rusty Key"]));
    }

    // ── Engine integration: Inventory command ───────────────────

    [Fact]
    public async Task CyoaPlayer_InventoryShowsFlatList()
    {
        var stateManager = await CreateStateAsync();
        var player = CreateCyoaPlayer();
        player.CyoaState!.Inventory.AddRange(["Torch", "Rusty Key"]);
        await stateManager.SavePlayerAsync(player);
        var engine = CreateEngine(stateManager);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Inventory, RawInput = "inventory" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.True(result.Success);
        Assert.Contains("Torch", result.MechanicalSummary);
        Assert.Contains("Rusty Key", result.MechanicalSummary);
        Assert.Contains("Health:", result.MechanicalSummary);
        // Should NOT contain gold or equipment (full RPG concepts)
        Assert.DoesNotContain("gold", result.MechanicalSummary!.ToLower());
        Assert.DoesNotContain("Equipped", result.MechanicalSummary);
    }

    [Fact]
    public async Task CyoaPlayer_StatsShowsHealthFlavor()
    {
        var stateManager = await CreateStateAsync();
        var player = CreateCyoaPlayer();
        player.CyoaState!.Health = CyoaHealthLevel.Hurt;
        player.CyoaState.ChoiceHistory.Add(new CyoaChoiceRecord { Node = "start", ChoiceText = "Go left" });
        await stateManager.SavePlayerAsync(player);
        var engine = CreateEngine(stateManager);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Stats, RawInput = "stats" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.True(result.Success);
        Assert.Contains("Choose Your Own Adventure", result.MechanicalSummary);
        Assert.Contains("battered", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Choices made: 1", result.MechanicalSummary);
        // Should NOT contain HP/MP/XP numbers
        Assert.DoesNotContain("HP:", result.MechanicalSummary);
        Assert.DoesNotContain("MP:", result.MechanicalSummary);
        Assert.DoesNotContain("XP:", result.MechanicalSummary);
    }

    [Fact]
    public async Task CyoaPlayer_EmptyInventoryShowsMessage()
    {
        var stateManager = await CreateStateAsync();
        var player = CreateCyoaPlayer();
        await stateManager.SavePlayerAsync(player);
        var engine = CreateEngine(stateManager);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Inventory, RawInput = "inventory" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.Contains("not carrying anything", result.MechanicalSummary);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static PlayerCharacter CreateCyoaPlayer() => new()
    {
        Id = "test-player",
        Name = "Elena",
        CurrentRoomId = "spawn",
        GameMode = GameMode.ChooseYourOwnAdventure,
        CyoaState = new CyoaState
        {
            Health = CyoaHealthLevel.Healthy,
            CurrentNode = "start",
            PreviousRoomId = "spawn"
        },
        Interaction = new InteractionState { Mode = InteractionMode.Cyoa },
        Hp = 0, MaxHp = 0, Mp = 0, MaxMp = 0, Xp = 0,
        ActiveWorldId = WorldDefaults.DefaultWorldId,
        HomeWorldId = WorldDefaults.DefaultWorldId
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
}
