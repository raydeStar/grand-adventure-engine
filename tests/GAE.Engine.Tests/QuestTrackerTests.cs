using GAE.Core.Models;
using GAE.Engine.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class QuestTrackerTests
{
    private readonly ContentRegistryService _registry;
    private readonly QuestEngine _engine;
    private readonly QuestTracker _tracker;

    public QuestTrackerTests()
    {
        _registry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);
        SeedQuests(_registry);
        _engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance);
        _tracker = new QuestTracker(_engine, _registry, NullLogger<QuestTracker>.Instance);
    }

    [Fact]
    public async Task OnCombatRoundSurvivedAsync_AdvancesSurviveObjective()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "survive_quest");

        var first = await _tracker.OnCombatRoundSurvivedAsync(player, "arena");
        var second = await _tracker.OnCombatRoundSurvivedAsync(player, "arena");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(QuestStatus.ReadyToTurnIn, player.QuestLog[0].Status);
    }

    [Fact]
    public async Task OnConversationStartedAsync_DeliverRequiresItemAndConsumesIt()
    {
        var player = CreatePlayer();
        player.Inventory.Add(new InventoryItem { Id = "sealed_letter", Name = "Sealed Letter", Quantity = 1, Type = ItemType.QuestItem });
        _engine.AcceptQuest(player, "delivery_quest");

        var summary = await _tracker.OnConversationStartedAsync(player, new Npc { Id = "innkeeper_mara", Name = "Mara" });

        Assert.NotNull(summary);
        Assert.Empty(player.Inventory);
        Assert.Equal(QuestStatus.ReadyToTurnIn, player.QuestLog[0].Status);
    }

    [Fact]
    public async Task OnConversationStartedAsync_DeliverDoesNotAdvanceWithoutItem()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "delivery_quest");

        var summary = await _tracker.OnConversationStartedAsync(player, new Npc { Id = "innkeeper_mara", Name = "Mara" });

        Assert.Null(summary);
        Assert.Equal(QuestStatus.Active, player.QuestLog[0].Status);
        Assert.False(player.QuestLog[0].Objectives[0].IsComplete);
    }

    [Fact]
    public async Task OnRoomEnteredAsync_CompletesEscortObjectiveWhenNpcArrives()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "escort_quest");
        var destination = new Room
        {
            Id = "town_gate",
            Name = "Town Gate",
            Npcs = [new Npc { Id = "ranger_thorne", Name = "Red XIII", Hp = 20, MaxHp = 20 }]
        };

        var summary = await _tracker.OnRoomEnteredAsync(player, destination);

        Assert.NotNull(summary);
        Assert.Contains("Escort objective complete", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuestStatus.ReadyToTurnIn, player.QuestLog[0].Status);
    }

    [Fact]
    public async Task ProcessNarratorQuestUpdatesAsync_FailsQuestWhenRecommended()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "failure_quest");
        var updates = new QuestUpdates();
        updates.FailureRecommended["failure_quest"] = "You burned the treaty.";

        var (summary, reward) = await _tracker.ProcessNarratorQuestUpdatesAsync(player, updates);

        Assert.Null(reward);
        Assert.NotNull(summary);
        Assert.Contains("Failed quest", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(QuestStatus.Failed, player.QuestLog[0].Status);
    }

    private static PlayerCharacter CreatePlayer() => new()
    {
        Id = "tracker-player",
        Name = "Tracker Hero",
        Race = "Human",
        Class = "Warrior",
        CurrentRoomId = "arena",
        Level = 3,
        Hp = 20,
        MaxHp = 20,
        Mp = 10,
        MaxMp = 10,
        Gold = 10
    };

    private static void SeedQuests(ContentRegistryService registry)
    {
        registry.Quests.Register(new QuestDefinition
        {
            Id = "survive_quest",
            Name = "Hold Fast",
            GiverId = "innkeeper_mara",
            Stages =
            [
                new QuestStage
                {
                    Id = "survive_stage",
                    Name = "Hold the Line",
                    Objectives =
                    [
                        new QuestObjective { Id = "survive_rounds", Type = ObjectiveType.Survive, TargetId = "arena", RequiredCount = 2 }
                    ]
                }
            ]
        });

        registry.Quests.Register(new QuestDefinition
        {
            Id = "delivery_quest",
            Name = "Special Delivery",
            GiverId = "courier",
            TurnInNpcId = "innkeeper_mara",
            Stages =
            [
                new QuestStage
                {
                    Id = "delivery_stage",
                    Name = "Hand Over the Letter",
                    Objectives =
                    [
                        new QuestObjective
                        {
                            Id = "deliver_letter",
                            Type = ObjectiveType.Deliver,
                            TargetId = "innkeeper_mara",
                            RequiredItemId = "sealed_letter",
                            RequiredCount = 1,
                            Description = "Deliver the sealed letter to Mara"
                        }
                    ]
                }
            ]
        });

        registry.Quests.Register(new QuestDefinition
        {
            Id = "escort_quest",
            Name = "Guide the Ranger",
            GiverId = "gate_guard_lena",
            TurnInNpcId = "gate_guard_lena",
            Stages =
            [
                new QuestStage
                {
                    Id = "escort_stage",
                    Name = "Return to the Gate",
                    Objectives =
                    [
                        new QuestObjective
                        {
                            Id = "escort_ranger",
                            Type = ObjectiveType.Escort,
                            TargetId = "ranger_thorne",
                            TargetName = "Red XIII",
                            LocationConstraint = "town_gate",
                            Description = "Escort Red XIII back to the gate"
                        }
                    ]
                }
            ]
        });

        registry.Quests.Register(new QuestDefinition
        {
            Id = "failure_quest",
            Name = "Diplomatic Disaster",
            GiverId = "innkeeper_mara",
            FailureHint = "The treaty is ash and the room goes cold.",
            Stages =
            [
                new QuestStage
                {
                    Id = "failure_stage",
                    Name = "Guard the Treaty",
                    Objectives = [new QuestObjective { Id = "keep_safe", Type = ObjectiveType.Custom, CustomCondition = "Do not ruin the treaty." }]
                }
            ]
        });
    }
}