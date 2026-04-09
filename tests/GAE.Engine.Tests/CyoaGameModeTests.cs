using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace GAE.Engine.Tests;

public class CyoaGameModeTests
{
    // ── CYOA Start ──────────────────────────────────────────────

    [Fact]
    public async Task CyoaStart_SetsGameModeAndInitializesState()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaStart, RawInput = "cyoa start" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.True(result.Success);
        Assert.Contains("Choose Your Own Adventure started", result.MechanicalSummary);

        var updated = await state.GetPlayerAsync(player.Id);
        Assert.NotNull(updated);
        Assert.Equal(GameMode.ChooseYourOwnAdventure, updated.GameMode);
        Assert.NotNull(updated.CyoaState);
        Assert.Equal(CyoaHealthLevel.Healthy, updated.CyoaState.Health);
        Assert.Equal("start", updated.CyoaState.CurrentNode);
        Assert.Empty(updated.CyoaState.Inventory);
        Assert.Empty(updated.CyoaState.ChoiceHistory);
        Assert.Empty(updated.CyoaState.SavePoints);
        Assert.Equal(InteractionMode.Cyoa, updated.Interaction.Mode);
    }

    [Fact]
    public async Task CyoaStart_SavesPreviousRoomId()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        player.CurrentRoomId = "tavern";
        await state.SavePlayerAsync(player);
        await state.SaveRoomAsync(new Room { Id = "tavern", Name = "The Tavern" });
        var engine = CreateEngine(state);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaStart, RawInput = "cyoa start" };
        await engine.ProcessActionAsync(player.Id, action);

        var updated = await state.GetPlayerAsync(player.Id);
        Assert.Equal("tavern", updated!.CyoaState!.PreviousRoomId);
    }

    [Fact]
    public async Task CyoaStart_WhenAlreadyInCyoa_Fails()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        player.GameMode = GameMode.ChooseYourOwnAdventure;
        player.CyoaState = new CyoaState { Health = CyoaHealthLevel.Healthy };
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaStart, RawInput = "cyoa start" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.False(result.Success);
        Assert.Contains("already in a Choose Your Own Adventure", result.MechanicalSummary);
    }

    // ── CYOA End ────────────────────────────────────────────────

    [Fact]
    public async Task CyoaEnd_RestoresFullRpgMode()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        player.GameMode = GameMode.ChooseYourOwnAdventure;
        player.CyoaState = new CyoaState
        {
            Health = CyoaHealthLevel.Hurt,
            PreviousRoomId = "tavern",
            ChoiceHistory =
            [
                new CyoaChoiceRecord { Node = "start", ChoiceText = "Go left" },
                new CyoaChoiceRecord { Node = "fork", ChoiceText = "Open the door" }
            ]
        };
        player.Interaction.Mode = InteractionMode.Cyoa;
        await state.SavePlayerAsync(player);
        await state.SaveRoomAsync(new Room { Id = "tavern", Name = "The Tavern" });
        var engine = CreateEngine(state);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaEnd, RawInput = "cyoa end" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.True(result.Success);
        Assert.Contains("2 choice(s)", result.MechanicalSummary);

        var updated = await state.GetPlayerAsync(player.Id);
        Assert.NotNull(updated);
        Assert.Equal(GameMode.FullRpg, updated.GameMode);
        Assert.Null(updated.CyoaState);
        Assert.Equal(InteractionMode.Explore, updated.Interaction.Mode);
        Assert.Equal("tavern", updated.CurrentRoomId);
    }

    [Fact]
    public async Task CyoaEnd_WhenNotInCyoa_Fails()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        var action = new GameAction { PlayerId = player.Id, Type = ActionType.CyoaEnd, RawInput = "cyoa end" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.False(result.Success);
        Assert.Contains("not in a Choose Your Own Adventure", result.MechanicalSummary);
    }

    // ── CYOA player has no HP/MP/XP fields populated ────────────

    [Fact]
    public async Task CyoaPlayer_NoHpMpXpPopulated()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        // Zero out stats to simulate a fresh CYOA-only character
        player.Hp = 0;
        player.MaxHp = 0;
        player.Mp = 0;
        player.MaxMp = 0;
        player.Xp = 0;
        player.Level = 1;
        player.GameMode = GameMode.ChooseYourOwnAdventure;
        player.CyoaState = new CyoaState();
        player.Interaction.Mode = InteractionMode.Cyoa;
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        // ProcessActionAsync should NOT recalculate MaxHP/MaxMP for CYOA players
        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Look, RawInput = "look" };
        await engine.ProcessActionAsync(player.Id, action);

        var updated = await state.GetPlayerAsync(player.Id);
        Assert.Equal(0, updated!.MaxHp);
        Assert.Equal(0, updated.MaxMp);
        Assert.Equal(0, updated.Xp);
    }

    // ── Mode flag prevents full-RPG mechanics ───────────────────

    [Fact]
    public async Task CyoaPlayer_LevelUpDoesNotFire()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        player.GameMode = GameMode.ChooseYourOwnAdventure;
        player.CyoaState = new CyoaState();
        player.Xp = 99999; // Would cause multiple level-ups in full RPG
        player.Level = 1;
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        var levelUp = engine.CheckAndApplyLevelUp(player);

        Assert.Null(levelUp);
        Assert.Equal(1, player.Level);
        Assert.Equal(99999, player.Xp); // XP untouched
    }

    [Fact]
    public async Task CyoaPlayer_DeadPlayerAutoRespawn_Skipped()
    {
        var state = await CreateStateAsync();
        var player = CreatePlayer();
        player.GameMode = GameMode.ChooseYourOwnAdventure;
        player.CyoaState = new CyoaState { Health = CyoaHealthLevel.Dead };
        player.Hp = 0; // Dead in RPG terms
        player.Interaction.Mode = InteractionMode.Cyoa;
        await state.SavePlayerAsync(player);
        var engine = CreateEngine(state);

        // In CYOA mode, the dead-player auto-respawn logic should not fire
        var action = new GameAction { PlayerId = player.Id, Type = ActionType.Look, RawInput = "look" };
        var result = await engine.ProcessActionAsync(player.Id, action);

        // Should NOT contain the death respawn message
        Assert.DoesNotContain("defeated", result.MechanicalSummary ?? "");
        // Player should still be at HP 0 — CYOA doesn't use HP
        var updated = await state.GetPlayerAsync(player.Id);
        Assert.Equal(0, updated!.Hp);
    }

    // ── CommandParser recognizes CYOA commands ──────────────────

    [Theory]
    [InlineData("cyoa start", ActionType.CyoaStart)]
    [InlineData("CYOA START", ActionType.CyoaStart)]
    [InlineData("book start", ActionType.CyoaStart)]
    [InlineData("cyoa end", ActionType.CyoaEnd)]
    [InlineData("cyoa stop", ActionType.CyoaEnd)]
    [InlineData("book quit", ActionType.CyoaEnd)]
    [InlineData("book finish", ActionType.CyoaEnd)]
    public void Parser_RecognizesCyoaCommands(string input, ActionType expectedType)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", input);
        Assert.Equal(expectedType, action.Type);
    }

    // ── State model integrity ───────────────────────────────────

    [Fact]
    public void CyoaState_DefaultValues()
    {
        var state = new CyoaState();

        Assert.Equal(CyoaHealthLevel.Healthy, state.Health);
        Assert.Empty(state.Inventory);
        Assert.Equal(string.Empty, state.CurrentNode);
        Assert.Empty(state.SavePoints);
        Assert.Empty(state.ChoiceHistory);
        Assert.Equal(string.Empty, state.PreviousRoomId);
    }

    [Fact]
    public void GameMode_DefaultIsFullRpg()
    {
        var player = new PlayerCharacter();
        Assert.Equal(GameMode.FullRpg, player.GameMode);
        Assert.Null(player.CyoaState);
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static PlayerCharacter CreatePlayer() => new()
    {
        Id = "test-player",
        Name = "Tester",
        CurrentRoomId = "spawn",
        Hp = 20,
        MaxHp = 20,
        Mp = 10,
        MaxMp = 10,
        Level = 1,
        Xp = 0,
        Str = 10,
        Dex = 10,
        Con = 10,
        Int = 10,
        Wis = 10,
        Cha = 10,
        Gold = 50,
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
