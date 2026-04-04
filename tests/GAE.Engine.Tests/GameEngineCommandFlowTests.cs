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
        var updatedRoom = (await stateManager.GetPlayerRoomAsync(PlayerId, "gate"))!;
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

        var updatedRoom = (await stateManager.GetPlayerRoomAsync(PlayerId, "gate"))!;
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

        var updatedRoom = (await stateManager.GetPlayerRoomAsync(PlayerId, "gate"))!;
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

        var updatedRoom = (await stateManager.GetPlayerRoomAsync(PlayerId, "lab"))!;
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

    // ==================== P1: Mechanical Use Command ====================

    [Fact]
    public async Task UsePotion_RestoresHpMechanically()
    {
        var stateManager = await CreateStateAsync();
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Hp = 5; // Damage player
        player.Inventory.Add(new InventoryItem { Name = "Health Potion", IsConsumable = true, Effect = "Restores 10 HP", Quantity = 2 });
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "use health potion");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Restored", result.MechanicalSummary);

        player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(12, player!.Hp); // 5 + 10 clamped to MaxHp 12
        Assert.Single(player.Inventory); // 2 - 1 = 1 remaining
        Assert.Equal(1, player.Inventory[0].Quantity);
    }

    [Fact]
    public async Task UseManaPotion_RestoresMp()
    {
        var stateManager = await CreateStateAsync();
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Mp = 1;
        player.Inventory.Add(new InventoryItem { Name = "Mana Potion", IsConsumable = true, Effect = "Restores 5 MP", Quantity = 1 });
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "use mana potion");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(4, player!.Mp); // 1 + 5 clamped to MaxMp 4
        Assert.Empty(player.Inventory); // Last one consumed
    }

    [Fact]
    public async Task UseNonConsumable_FallsThroughToFreeForm()
    {
        var stateManager = await CreateStateAsync();
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Inventory.Add(new InventoryItem { Name = "Rusty Key", IsConsumable = false, Quantity = 1 });
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "use rusty key");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        // Should fall through to free-form, which succeeds with narration
        Assert.True(result.Success);
        Assert.Single(player.Inventory); // Not consumed
    }

    // ==================== P3: Buy / Sell System ====================

    [Fact]
    public async Task BuyItem_DeductsGoldAndAddsToInventory()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Market", Description = "A shop.",
            Npcs = [new Npc { Id = "merchant", Name = "Korga", IsShopkeeper = true,
                ShopInventory = [new InventoryItem { Name = "Iron Sword", Value = 50, Type = ItemType.Weapon, IsEquippable = true, Quantity = 5 }] }]
        };
        var stateManager = await CreateStateAsync(room);
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Gold = 100;
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "buy iron sword");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Bought", result.MechanicalSummary);

        player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(50, player!.Gold);
        Assert.Single(player.Inventory);
        Assert.Equal("Iron Sword", player.Inventory[0].Name);
    }

    [Fact]
    public async Task BuyItem_InsufficientGold_Fails()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Market", Description = "A shop.",
            Npcs = [new Npc { Id = "merchant", Name = "Korga", IsShopkeeper = true,
                ShopInventory = [new InventoryItem { Name = "Diamond Ring", Value = 500, Quantity = 1 }] }]
        };
        var stateManager = await CreateStateAsync(room);
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Gold = 10;
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "buy diamond ring");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("costs", result.MechanicalSummary);
        Assert.Equal(10, (await stateManager.GetPlayerAsync(PlayerId))!.Gold);
    }

    [Fact]
    public async Task SellItem_AddsGoldAtHalfValue()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Market", Description = "A shop.",
            Npcs = [new Npc { Id = "merchant", Name = "Korga", IsShopkeeper = true, ShopInventory = [] }]
        };
        var stateManager = await CreateStateAsync(room);
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Gold = 10;
        player.Inventory.Add(new InventoryItem { Name = "Old Dagger", Value = 20, Quantity = 1 });
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "sell old dagger");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Sold", result.MechanicalSummary);

        player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(20, player!.Gold); // 10 + (20/2)
        Assert.Empty(player.Inventory);
    }

    // ==================== P4: Auto-Equip on Take ====================

    [Fact]
    public async Task TakeEquippableItem_AutoEquipsWhenSlotEmpty()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Armory", Description = "An armory.",
            Items = [new InventoryItem { Name = "Steel Sword", Type = ItemType.Weapon, IsEquippable = true, Value = 30, Quantity = 1 }],
            Exits = new() { ["north"] = "corridor" }
        };
        var stateManager = await CreateStateAsync(room);
        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "take steel sword");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Equipped", result.MechanicalSummary);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.NotNull(player!.Equipment.Weapon);
        Assert.Equal("Steel Sword", player.Equipment.Weapon!.Name);
        Assert.Empty(player.Inventory); // Moved from inventory to equipment
    }

    [Fact]
    public async Task TakeEquippableItem_DoesNotAutoEquipWhenSlotOccupied()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Armory", Description = "An armory.",
            Items = [new InventoryItem { Name = "Steel Sword", Type = ItemType.Weapon, IsEquippable = true, Value = 30, Quantity = 1 }],
            Exits = new() { ["north"] = "corridor" }
        };
        var stateManager = await CreateStateAsync(room);
        var player = await stateManager.GetPlayerAsync(PlayerId);
        player!.Equipment.MainHand = new InventoryItem { Name = "Old Sword", Type = ItemType.Weapon, IsEquippable = true };
        await stateManager.SavePlayerAsync(player);

        var narrator = new PerpetualFallbackNarrator();
        var engine = CreateEngineWithDice(stateManager, narrator);

        var action = engine.ParseCommand(PlayerId, "take steel sword");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.DoesNotContain("Equipped", result.MechanicalSummary);

        player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal("Old Sword", player!.Equipment.Weapon!.Name); // Still equipped
        Assert.Single(player.Inventory); // In inventory, not equipped
    }

    // ==================== P2: Multi-Enemy Combat ====================

    [Fact]
    public async Task Attack_GathersAllHostilesIntoCombat()
    {
        var room = new Room
        {
            Id = "spawn", Name = "Ambush Site", Description = "Enemies surround you.",
            Npcs =
            [
                new Npc { Id = "bandit1", Name = "Bandit", IsHostile = true, Hp = 10, MaxHp = 10, Defense = 8, DamageDice = "1d4", AttackBonus = 2 },
                new Npc { Id = "bandit2", Name = "Thug", IsHostile = true, Hp = 8, MaxHp = 8, Defense = 7, DamageDice = "1d4", AttackBonus = 1 }
            ],
            Exits = new() { ["north"] = "corridor" }
        };
        var stateManager = await CreateStateAsync(room);
        var narrator = new PerpetualFallbackNarrator();

        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        dice.Setup(d => d.RollAttack(It.IsAny<int>())).Returns(new DiceRoll { Expression = "1d20+1", Total = 15, IndividualRolls = [14] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>())).Returns(new DiceRoll { Expression = "1d4+1", Total = 4, IndividualRolls = [3] });
        dice.Setup(d => d.Roll(It.IsAny<string>())).Returns(new DiceRoll { Expression = "1d4", Total = 3, IndividualRolls = [3] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>())).Returns(new DiceRoll { Expression = "1d100", Total = 99, IndividualRolls = [99] });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>())).Returns(new DiceRoll { Expression = "1d20", Total = 10, IndividualRolls = [10] });

        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(stateManager, dice.Object, narrator, parser, new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var action = engine.ParseCommand(PlayerId, "attack bandit");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        // Should be in combat mode
        Assert.Equal(InteractionMode.Combat, player!.Interaction.Mode);

        // CombatState should exist with both enemies + player
        var combat = await stateManager.GetCombatStateAsync($"{PlayerId}:{room.Id}");
        Assert.NotNull(combat);
        Assert.True(combat!.TurnOrder.Count >= 2); // At least player + surviving enemies
    }

    // ==================== CommandParser: Buy/Sell ====================

    [Fact]
    public void ParseCommand_BuyItem_ReturnsCorrectAction()
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", "buy health potion");

        Assert.Equal(ActionType.Buy, action.Type);
        Assert.Equal("health potion", action.Target);
    }

    [Fact]
    public void ParseCommand_SellItem_ReturnsCorrectAction()
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", "sell old dagger");

        Assert.Equal(ActionType.Sell, action.Type);
        Assert.Equal("old dagger", action.Target);
    }

    [Fact]
    public void ParseCommand_PurchaseItem_ParsesAsBuy()
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse("test-player", "purchase iron sword");

        Assert.Equal(ActionType.Buy, action.Type);
        Assert.Equal("iron sword", action.Target);
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

    private static GameEngine CreateEngineWithDice(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new ProbabilityEngine(NullLogger<ProbabilityEngine>.Instance);
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(stateManager, dice, narrator, parser, new GameRulesConfig(), NullLogger<GameEngine>.Instance);
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

        public Task<Room> GenerateDungeonEntranceAsync(string dungeonId, int playerLevel, Room sourceRoom, CancellationToken ct = default)
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


        public Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(string playerDescription, string? previousSheet, CancellationToken ct = default)
            => Task.FromResult<CharacterCreationAiResponse?>(null);

        public Task<GAE.Core.Registry.ImprovisedSpellResult> EvaluateImprovisedSpellAsync(
            PlayerCharacter player, Room room, string spellName, string? target,
            int powerCap, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
            => Task.FromResult(new GAE.Core.Registry.ImprovisedSpellResult
            {
                PowerLevel = powerCap + 1,
                PlayerCap = powerCap,
                Success = false,
                ManaCost = 1,
                Narration = $"The spell fizzles."
            });

        public Task<string> GenerateContentAsync(string contentType, string description, string? existingJson, CancellationToken ct = default)
            => Task.FromResult("{}");

        public string GetActiveModel() => "test-model";
        public void SetActiveModel(string model) { }
        public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
    }
}
