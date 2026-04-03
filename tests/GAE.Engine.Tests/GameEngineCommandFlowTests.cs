using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class GameEngineCommandFlowTests
{
    private const string PlayerId = "test-player";

    [Fact]
    public async Task ProcessActionAsync_Help_PersistsStoryEntryWithRawInput()
    {
        var stateManager = await CreateStateAsync();
        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var engine = CreateEngine(stateManager, narrator.Object);

        var action = engine.ParseCommand(PlayerId, "help");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Equal("help", result.RawInput);

        var entries = await stateManager.GetStoryEntriesAsync(PlayerId, 10);
        var entry = Assert.Single(entries);
        Assert.Equal("help", entry.RawInput);
        Assert.Contains("Available Commands", entry.MechanicalSummary);
    }

    [Fact]
    public async Task ProcessActionAsync_LookAtPluralTarget_MatchesRepresentativeNpc()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "qa-lab",
            Name = "QA Lab",
            Description = "A repeatable manual test fixture room.",
            Npcs = [new Npc { Id = "sentinel-1", Name = "Sentinel" }],
            Items = [new InventoryItem { Id = "token", Name = "Inspection Token", Quantity = 2 }],
            Exits = new Dictionary<string, string> { ["south"] = "spawn" }
        });

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated look.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "look at sentinels");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Equal("look at sentinels", result.RawInput);
        Assert.Equal("Narrated look.", result.Narration);

        var entries = await stateManager.GetStoryEntriesAsync(PlayerId, 10);
        var entry = Assert.Single(entries);
        Assert.Equal("look at sentinels", entry.RawInput);
        narrator.Verify(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessActionAsync_FailedMove_StillUsesNarrationAndPersistsStory()
    {
        var stateManager = await CreateStateAsync();
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The dark stone gives nothing back but a cold refusal.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "go west");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Equal("The dark stone gives nothing back but a cold refusal.", result.Narration);

        var entries = await stateManager.GetStoryEntriesAsync(PlayerId, 10);
        var entry = Assert.Single(entries);
        Assert.Equal("go west", entry.RawInput);
        Assert.Equal(result.Narration, entry.Narration);
        narrator.Verify(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessActionAsync_FreeFormFallback_RemainsPlayableAndPersistsStory()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate with iron plates.",
            Npcs = [new Npc { Id = "warden", Name = "Gate Warden" }]
        });

        var narrator = new PerpetualFallbackNarrator();

        var engine = CreateEngine(stateManager, narrator);
        var action = engine.ParseCommand(PlayerId, "I want to shine the rusted gate");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Equal("I want to shine the rusted gate", result.RawInput);
        Assert.Contains("rusted gate", result.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrator", result.Narration, StringComparison.OrdinalIgnoreCase);

        var entries = await stateManager.GetStoryEntriesAsync(PlayerId, 10);
        var entry = Assert.Single(entries);
        Assert.Equal("I want to shine the rusted gate", entry.RawInput);
        Assert.Equal(result.Narration, entry.Narration);
    }

    [Fact]
    public async Task ProcessActionAsync_DropItem_RemovesItFromInventoryAndPlacesItInRoom()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate with iron plates."
        });

        var player = (await stateManager.GetPlayerAsync(PlayerId))!;
        player.Inventory.Add(new InventoryItem { Id = "rope", Name = "Silken Rope", Quantity = 1, Type = ItemType.Misc });
        await stateManager.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated drop.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "drop silken rope");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        var updatedPlayer = (await stateManager.GetPlayerAsync(PlayerId))!;
        Assert.Empty(updatedPlayer.Inventory);
        var updatedRoom = (await stateManager.GetRoomAsync("gate"))!;
        var droppedItem = Assert.Single(updatedRoom.Items);
        Assert.Equal("Silken Rope", droppedItem.Name);
        Assert.Equal(1, droppedItem.Quantity);
    }

    [Fact]
    public async Task ProcessActionAsync_ThrowGoldOnGround_RemovesGoldAndPlacesCoinInRoom()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate with iron plates."
        });

        var player = (await stateManager.GetPlayerAsync(PlayerId))!;
        player.Gold = 50;
        await stateManager.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated drop.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "throw 1 gold on the ground");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Equal(-1, result.GoldChange);

        var updatedPlayer = (await stateManager.GetPlayerAsync(PlayerId))!;
        Assert.Equal(49, updatedPlayer.Gold);

        var updatedRoom = (await stateManager.GetRoomAsync("gate"))!;
        var droppedCoin = Assert.Single(updatedRoom.Items);
        Assert.Equal("Gold Coin", droppedCoin.Name);
        Assert.Equal(1, droppedCoin.Quantity);
    }

    [Fact]
    public async Task ProcessActionAsync_TakeItem_RemovesFromRoomAndAddsToInventory()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate with iron plates.",
            Items = [new InventoryItem { Id = "rope", Name = "Silken Rope", Quantity = 1, Type = ItemType.Misc }]
        });

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated take.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "take silken rope");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Silken Rope", result.MechanicalSummary);
        Assert.Contains("added to inventory", result.MechanicalSummary);
        var gained = Assert.Single(result.ItemsGained);
        Assert.Equal("Silken Rope", gained.Name);

        var updatedPlayer = (await stateManager.GetPlayerAsync(PlayerId))!;
        var invItem = Assert.Single(updatedPlayer.Inventory);
        Assert.Equal("Silken Rope", invItem.Name);

        var updatedRoom = (await stateManager.GetRoomAsync("gate"))!;
        Assert.Empty(updatedRoom.Items);
    }

    [Fact]
    public async Task ProcessActionAsync_PickUpMultipleStacks_GathersAllMatchingItems()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "lab",
            Name = "QA Lab",
            Description = "A test room.",
            Items =
            [
                new InventoryItem { Id = "t1", Name = "Inspection Token", Quantity = 1, Type = ItemType.Misc },
                new InventoryItem { Id = "t2", Name = "Inspection Token", Quantity = 1, Type = ItemType.Misc },
                new InventoryItem { Id = "t3", Name = "Inspection Token", Quantity = 1, Type = ItemType.Misc }
            ]
        });

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated take.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "pick up inspection tokens");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Inspection Token (x3)", result.MechanicalSummary);

        var updatedPlayer = (await stateManager.GetPlayerAsync(PlayerId))!;
        var invItem = Assert.Single(updatedPlayer.Inventory);
        Assert.Equal("Inspection Token", invItem.Name);
        Assert.Equal(3, invItem.Quantity);

        var updatedRoom = (await stateManager.GetRoomAsync("lab"))!;
        Assert.Empty(updatedRoom.Items);
    }

    [Fact]
    public async Task ProcessActionAsync_TakeNonexistentItem_FailsGracefully()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate."
        });

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "take diamond");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("diamond", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessActionAsync_PickUpGoldCoins_AddsToGoldTotal()
    {
        var stateManager = await CreateStateAsync(room: new Room
        {
            Id = "gate",
            Name = "Ironhold Gate",
            Description = "A rusted war gate.",
            Items = [new InventoryItem { Id = "gc", Name = "Gold Coin", Quantity = 5, Type = ItemType.Misc }]
        });

        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(service => service.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narrated take.");

        var engine = CreateEngine(stateManager, narrator.Object);
        var action = engine.ParseCommand(PlayerId, "pick up gold");

        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Equal(5, result.GoldChange);
        Assert.Contains("5 gold", result.MechanicalSummary);

        var updatedPlayer = (await stateManager.GetPlayerAsync(PlayerId))!;
        Assert.Equal(5, updatedPlayer.Gold); // 0 starting + 5 picked up
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(
            stateManager,
            dice.Object,
            narrator,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static async Task<InMemoryStateManager> CreateStateAsync(Room? room = null)
    {
        var stateManager = new InMemoryStateManager();
        await stateManager.SaveRoomAsync(room ?? new Room
        {
            Id = "spawn",
            Name = "The Crossroads Inn",
            Description = "A weathered inn at the junction of three ancient roads.",
            Exits = new Dictionary<string, string>
            {
                ["north"] = "pine-road",
                ["south"] = "river-step"
            }
        });

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Playwright Hero",
            Race = "Human",
            Class = "Warrior",
            Level = 1,
            CurrentRoomId = room?.Id ?? "spawn",
            Hp = 12,
            MaxHp = 12,
            Mp = 4,
            MaxMp = 4,
            Str = 12,
            Dex = 10,
            Con = 11,
            Int = 10,
            Wis = 10,
            Cha = 10
        });

        return stateManager;
    }

    private class PerpetualFallbackNarrator : INarratorService
    {
        public Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
            => Task.FromResult(context.MechanicalResult.Narration ?? context.MechanicalResult.MechanicalSummary);

        public Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Npc> GenerateNpcAsync(Room room, string? faction = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> GenerateAsciiArtAsync(string subject, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> ParseIntentAsync(string rawInput, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<FreeFormResponse> ProcessFreeFormAsync(PlayerCharacter player, Room room, string rawInput, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
            => Task.FromResult(new FreeFormResponse
            {
                Success = true,
                Narration = "Thorin sets to work on the rusted gate, rubbing at age and grime until the effort teases out a brief hint of order from the wear. In Ironhold Gate, it changes no fortunes, but it leaves the scene feeling tended rather than ignored. Gate Warden clocks the gesture, then returns their attention to the wider room."
            });

        public Task<FreeFormResponse> ProcessConversationTurnAsync(PlayerCharacter player, Room room, Npc npc, InteractionState interaction, string rawInput, CancellationToken ct = default)
            => Task.FromResult(new FreeFormResponse
            {
                Success = true,
                Narration = $"{npc.Name} regards you thoughtfully."
            });

        public Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default)
            => Task.FromResult(new FreeFormResponse
            {
                Success = true,
                Narration = $"You exchange blows with {enemy.Name}.",
                InteractionUpdate = new InteractionUpdate
                {
                    Mode = InteractionMode.Combat,
                    CombatStatus = "ongoing",
                    EnemyUpdate = new Dictionary<string, int> { ["hp"] = -3 }
                }
            });

        public string GetActiveModel() => "test-model";
        public void SetActiveModel(string model) { }
        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
    }
}