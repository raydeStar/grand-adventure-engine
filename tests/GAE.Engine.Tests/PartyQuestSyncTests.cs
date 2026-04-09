using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.Registry;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Tests for X02: Party quest sync — broadcasting updates to party members,
/// pending notifications for offline players, and notification drain in GameEngine.
/// </summary>
public class PartyQuestSyncTests
{
    private readonly ContentRegistryService _registry;

    public PartyQuestSyncTests()
    {
        _registry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);
        SeedPartyQuest(_registry);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Broadcast — AdvanceObjectiveAsync notifies other party members
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AdvanceObjective_BroadcastsQuestUpdateToOtherPartyMembers()
    {
        var state = new InMemoryStateManager();
        var broadcaster = new Mock<IGameEventBroadcaster>();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state, broadcaster.Object);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        await engine.AdvanceObjectiveAsync(p1, "party_hunt", "party_wolves", 2);

        // Player 2 should receive a broadcast event
        broadcaster.Verify(b => b.BroadcastEventAsync(
            It.Is<GameEvent>(e =>
                e.Type == GameEventType.QuestUpdated &&
                e.PlayerId == p2.Id &&
                e.Summary.Contains("Party Hunt")),
            It.IsAny<CancellationToken>()), Times.Once);

        // Player 1 (the acting player) should NOT receive a broadcast
        broadcaster.Verify(b => b.BroadcastEventAsync(
            It.Is<GameEvent>(e => e.PlayerId == p1.Id),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AdvanceObjective_AddsPendingNotificationToOtherPartyMembers()
    {
        var state = new InMemoryStateManager();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        await engine.AdvanceObjectiveAsync(p1, "party_hunt", "party_wolves", 1);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        Assert.Single(updatedP2.PendingNotifications);
        Assert.Contains("Party Hunt", updatedP2.PendingNotifications[0]);

        // Acting player should have no pending notifications
        var updatedP1 = await state.GetPlayerAsync(p1.Id);
        Assert.NotNull(updatedP1);
        Assert.Empty(updatedP1.PendingNotifications);
    }

    [Fact]
    public async Task AdvanceObjective_NotificationIncludesObjectiveProgress()
    {
        var state = new InMemoryStateManager();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        await engine.AdvanceObjectiveAsync(p1, "party_hunt", "party_wolves", 2);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        // Notification should mention progress fraction — but not ready to turn in yet (2/3)
        Assert.Contains("progress updated", updatedP2.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdvanceObjective_CompletionNotifiesReadyToTurnIn()
    {
        var state = new InMemoryStateManager();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        // Complete the objective fully
        await engine.AdvanceObjectiveAsync(p1, "party_hunt", "party_wolves", 3);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        Assert.Single(updatedP2.PendingNotifications);
        Assert.Contains("ready to turn in", updatedP2.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Broadcast — FailQuestAsync notifies party members
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FailQuest_BroadcastsFailureToOtherPartyMembers()
    {
        var state = new InMemoryStateManager();
        var broadcaster = new Mock<IGameEventBroadcaster>();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state, broadcaster.Object);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        await engine.FailQuestAsync(p1, "party_hunt", "Dire wolves escaped.");

        broadcaster.Verify(b => b.BroadcastEventAsync(
            It.Is<GameEvent>(e =>
                e.Type == GameEventType.QuestUpdated &&
                e.PlayerId == p2.Id &&
                e.Summary.Contains("failed")),
            It.IsAny<CancellationToken>()), Times.Once);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        Assert.Single(updatedP2.PendingNotifications);
        Assert.Contains("failed", updatedP2.PendingNotifications[0], StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Broadcast — TurnInQuestAsync notifies party members
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TurnInQuest_BroadcastsCompletionToOtherPartyMembers()
    {
        var state = new InMemoryStateManager();
        var broadcaster = new Mock<IGameEventBroadcaster>();
        var engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance, state, broadcaster.Object);
        var (p1, p2) = await SetupPartyAsync(state, engine);

        // Complete the quest objectives
        await engine.AdvanceObjectiveAsync(p1, "party_hunt", "party_wolves", 3);

        // Reset broadcast tracking for clarity
        broadcaster.Invocations.Clear();

        // Turn in
        var reward = await engine.TurnInQuestAsync(p1, "party_hunt", new Npc { Id = "innkeeper_mara", Name = "Mara" });
        Assert.NotNull(reward);

        // Player 2 should get a completion broadcast
        broadcaster.Verify(b => b.BroadcastEventAsync(
            It.Is<GameEvent>(e =>
                e.Type == GameEventType.QuestUpdated &&
                e.PlayerId == p2.Id &&
                e.Summary.Contains("completed")),
            It.IsAny<CancellationToken>()), Times.Once);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        // Should have 2 notifications: one from AdvanceObjective (ready to turn in), one from TurnIn (completed)
        Assert.Contains(updatedP2.PendingNotifications, n => n.Contains("completed", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Notification drain — GameEngine.ProcessActionAsync delivers them
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessAction_DrainsNotificationsIntoActionResult()
    {
        var state = new InMemoryStateManager();
        var player = CreatePlayer("drain-tester");
        player.PendingNotifications.Add("📜 Party quest \"Party Hunt\" progress updated (1/1 objectives).");
        player.PendingNotifications.Add("📜 Party quest \"Party Hunt\" is ready to turn in!");
        await state.SavePlayerAsync(player);
        await state.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "Tavern",
            Description = "A warm tavern.",
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        });

        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You look around.");
        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(state, dice.Object, narrator.Object, parser,
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var action = engine.ParseCommand(player.Id, "look");
        var result = await engine.ProcessActionAsync(player.Id, action);

        // Notifications should be attached to the result
        Assert.Equal(2, result.Notifications.Count);
        Assert.Contains("progress updated", result.Notifications[0]);
        Assert.Contains("ready to turn in", result.Notifications[1]);

        // Player's pending list should now be empty
        var updated = await state.GetPlayerAsync(player.Id);
        Assert.NotNull(updated);
        Assert.Empty(updated.PendingNotifications);
    }

    [Fact]
    public async Task ProcessAction_NoNotifications_ReturnsEmptyList()
    {
        var state = new InMemoryStateManager();
        var player = CreatePlayer("clean-player");
        await state.SavePlayerAsync(player);
        await state.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "Tavern",
            Description = "A warm tavern.",
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        });

        var narrator = new Mock<INarratorService>();
        narrator.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("You look around.");
        var dice = new Mock<IProbabilityEngine>();
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var engine = new GameEngine(state, dice.Object, narrator.Object, parser,
            new GameRulesConfig(), NullLogger<GameEngine>.Instance);

        var action = engine.ParseCommand(player.Id, "look");
        var result = await engine.ProcessActionAsync(player.Id, action);

        Assert.Empty(result.Notifications);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Edge case — CompleteCustomObjectiveAsync also broadcasts
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompleteCustomObjective_NotifiesOtherPartyMembers()
    {
        var state = new InMemoryStateManager();
        var broadcaster = new Mock<IGameEventBroadcaster>();

        // Seed a party quest with a custom objective
        var customRegistry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);
        customRegistry.Quests.Register(new QuestDefinition
        {
            Id = "custom_party",
            Name = "Custom Party Task",
            GiverId = "innkeeper_mara",
            IsPartyQuest = true,
            Stages =
            [
                new QuestStage
                {
                    Id = "custom_stage",
                    Name = "Do It",
                    Objectives =
                    [
                        new QuestObjective { Id = "custom_obj", Type = ObjectiveType.Custom, CustomCondition = "Do something" }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 50 }
        });

        var engine = new QuestEngine(customRegistry, NullLogger<QuestEngine>.Instance, state, broadcaster.Object);
        var p1 = CreatePlayer("custom-p1");
        var p2 = CreatePlayer("custom-p2");
        await state.SavePlayerAsync(p1);
        await state.SavePlayerAsync(p2);

        await engine.AcceptQuestAsync(p1, "custom_party");
        await state.SavePlayerAsync(p1);
        await engine.AcceptQuestAsync(p2, "custom_party");
        await state.SavePlayerAsync(p2);

        broadcaster.Invocations.Clear();

        await engine.CompleteCustomObjectiveAsync(p1, "custom_party", "custom_obj");

        broadcaster.Verify(b => b.BroadcastEventAsync(
            It.Is<GameEvent>(e =>
                e.Type == GameEventType.QuestUpdated &&
                e.PlayerId == p2.Id),
            It.IsAny<CancellationToken>()), Times.Once);

        var updatedP2 = await state.GetPlayerAsync(p2.Id);
        Assert.NotNull(updatedP2);
        Assert.Single(updatedP2.PendingNotifications);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static PlayerCharacter CreatePlayer(string id = "test-player") => new()
    {
        Id = id,
        Name = $"Hero {id}",
        Race = "Human",
        Class = "Warrior",
        Level = 3,
        CurrentRoomId = "tavern",
        Hp = 12, MaxHp = 12,
        Mp = 4, MaxMp = 4,
        Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10,
        Gold = 100
    };

    private async Task<(PlayerCharacter p1, PlayerCharacter p2)> SetupPartyAsync(
        InMemoryStateManager state, QuestEngine engine)
    {
        var p1 = CreatePlayer("player-one");
        var p2 = CreatePlayer("player-two");
        await state.SavePlayerAsync(p1);
        await state.SavePlayerAsync(p2);

        await engine.AcceptQuestAsync(p1, "party_hunt");
        await state.SavePlayerAsync(p1);
        await engine.AcceptQuestAsync(p2, "party_hunt");
        await state.SavePlayerAsync(p2);

        return (p1, p2);
    }

    private static void SeedPartyQuest(ContentRegistryService registry)
    {
        registry.Quests.Register(new QuestDefinition
        {
            Id = "party_hunt",
            Name = "Party Hunt",
            Description = "A shared bounty.",
            GiverId = "innkeeper_mara",
            TurnInNpcId = "innkeeper_mara",
            IsPartyQuest = true,
            Stages =
            [
                new QuestStage
                {
                    Id = "hunt_stage",
                    Name = "Thin the Pack",
                    Objectives =
                    [
                        new QuestObjective { Id = "party_wolves", Type = ObjectiveType.Kill, TargetId = "dire_wolf", RequiredCount = 3 }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 90, Gold = 30 }
        });
    }
}
