using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class QuestEngineTests
{
    private readonly ContentRegistryService _registry;
    private readonly QuestEngine _engine;

    public QuestEngineTests()
    {
        _registry = new ContentRegistryService(NullLogger<ContentRegistryService>.Instance);
        SeedTestQuests(_registry);
        _engine = new QuestEngine(_registry, NullLogger<QuestEngine>.Instance);
    }

    // ── Accept ──

    [Fact]
    public void AcceptQuest_Succeeds_ForEligiblePlayer()
    {
        var player = CreatePlayer();
        var progress = _engine.AcceptQuest(player, "rat_problem");

        Assert.NotNull(progress);
        Assert.Equal("rat_problem", progress.QuestId);
        Assert.Equal(QuestStatus.Active, progress.Status);
        Assert.Equal("hunt_rats", progress.CurrentStageId);
        Assert.Single(progress.Objectives);
        Assert.Single(player.QuestLog);
    }

    [Fact]
    public void AcceptQuest_FailsIfAlreadyActive()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        var second = _engine.AcceptQuest(player, "rat_problem");

        Assert.Null(second);
        Assert.Single(player.QuestLog);
    }

    [Fact]
    public void AcceptQuest_FailsIfAlreadyCompleted_OneTime()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");
        // Manually complete
        player.QuestLog[0].Status = QuestStatus.Completed;

        var (canAccept, reason) = _engine.CanAcceptQuest(player, "rat_problem");

        Assert.False(canAccept);
        Assert.Contains("already completed", reason!);
    }

    [Fact]
    public void AcceptQuest_FailsIfPrerequisiteNotMet()
    {
        var player = CreatePlayer();

        var (canAccept, reason) = _engine.CanAcceptQuest(player, "korgas_test");

        Assert.False(canAccept);
        Assert.Contains("Rat Problem", reason!); // prerequisite quest name
    }

    [Fact]
    public void AcceptQuest_SucceedsAfterPrerequisiteMet()
    {
        var player = CreatePlayer();
        // Complete prerequisite
        player.QuestLog.Add(new QuestProgress { QuestId = "rat_problem", Status = QuestStatus.Completed });

        var progress = _engine.AcceptQuest(player, "korgas_test");

        Assert.NotNull(progress);
    }

    [Fact]
    public void AcceptQuest_FailsIfUnderLevel()
    {
        var player = CreatePlayer(level: 1);

        var (canAccept, reason) = _engine.CanAcceptQuest(player, "high_level_quest");

        Assert.False(canAccept);
        Assert.Contains("level", reason!);
    }

    // ── Abandon ──

    [Fact]
    public void AbandonQuest_SetsStatusToAbandoned()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        var result = _engine.AbandonQuest(player, "rat_problem");

        Assert.True(result);
        Assert.Equal(QuestStatus.Abandoned, player.QuestLog[0].Status);
    }

    [Fact]
    public void AbandonQuest_ReturnsFalseIfNotActive()
    {
        var player = CreatePlayer();

        Assert.False(_engine.AbandonQuest(player, "rat_problem"));
    }

    // ── Advance Objective ──

    [Fact]
    public void AdvanceObjective_IncrementsCount()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 1);

        var obj = player.QuestLog[0].Objectives[0];
        Assert.Equal(1, obj.CurrentCount);
        Assert.False(obj.IsComplete);
    }

    [Fact]
    public void AdvanceObjective_CompletesObjectiveAtRequiredCount()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        for (int i = 0; i < 3; i++)
            _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 1);

        var obj = player.QuestLog[0].Objectives[0];
        Assert.True(obj.IsComplete);
    }

    [Fact]
    public void AdvanceObjective_DoesNotExceedRequiredCount()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 10);

        var obj = player.QuestLog[0].Objectives[0];
        Assert.Equal(3, obj.CurrentCount); // capped at RequiredCount
        Assert.True(obj.IsComplete);
    }

    [Fact]
    public void AdvanceObjective_AdvancesStageWhenAllObjectivesComplete()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "two_stage_quest");

        // Complete the first stage's only objective
        _engine.AdvanceObjective(player, "two_stage_quest", "find_thing", 1);

        var progress = player.QuestLog[0];
        Assert.Equal("stage_2", progress.CurrentStageId);
        Assert.Single(progress.Objectives);
        Assert.Equal("deliver_thing", progress.Objectives[0].ObjectiveId);
    }

    [Fact]
    public void AdvanceObjective_MarksQuestReadyToTurnIn_OnFinalStage()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 3);

        // rat_problem has one stage → final stage → ready to turn in
        Assert.Equal(QuestStatus.ReadyToTurnIn, player.QuestLog[0].Status);
    }

    // ── Custom Objectives ──

    [Fact]
    public void CompleteCustomObjective_MarksObjectiveComplete()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "custom_quest");

        var result = _engine.CompleteCustomObjective(player, "custom_quest", "custom_obj");

        Assert.True(result);
        var obj = player.QuestLog[0].Objectives[0];
        Assert.True(obj.IsComplete);
    }

    // ── Turn In ──

    [Fact]
    public void TurnInQuest_AppliesGoldAndXpRewards()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");
        _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 3);

        var npc = new Npc { Id = "innkeeper_mara", Name = "Mara" };
        var rewards = _engine.TurnInQuest(player, "rat_problem", npc);

        Assert.NotNull(rewards);
        Assert.Equal(50, rewards.Xp);
        Assert.Equal(10, rewards.Gold);
        Assert.Equal(QuestStatus.Completed, player.QuestLog[0].Status);
        Assert.Equal(50, player.Xp);
        Assert.Equal(110, player.Gold); // started with 100
    }

    [Fact]
    public void TurnInQuest_FailsIfNotReadyToTurnIn()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        var rewards = _engine.TurnInQuest(player, "rat_problem");

        Assert.Null(rewards);
    }

    [Fact]
    public void TurnInQuest_FailsIfWrongNpc()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");
        _engine.AdvanceObjective(player, "rat_problem", "kill_rats", 3);

        var wrongNpc = new Npc { Id = "guard_bram", Name = "Bram" };
        var rewards = _engine.TurnInQuest(player, "rat_problem", wrongNpc);

        Assert.Null(rewards);
    }

    // ── GetAvailableQuests ──

    [Fact]
    public void GetAvailableQuests_ReturnsQuestsForNpc()
    {
        var player = CreatePlayer();
        var npc = new Npc { Id = "innkeeper_mara", Name = "Mara", QuestsOffered = ["rat_problem"] };

        var quests = _engine.GetAvailableQuests(player, npc);

        Assert.Single(quests);
        Assert.Equal("rat_problem", quests[0].Id);
    }

    [Fact]
    public void GetAvailableQuests_ExcludesAlreadyActiveQuests()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        var npc = new Npc { Id = "innkeeper_mara", Name = "Mara", QuestsOffered = ["rat_problem"] };
        var quests = _engine.GetAvailableQuests(player, npc);

        Assert.Empty(quests);
    }

    // ── Journal Formatting ──

    [Fact]
    public void FormatJournal_ShowsActiveAndCompletedQuests()
    {
        var player = CreatePlayer();
        _engine.AcceptQuest(player, "rat_problem");

        var journal = _engine.FormatJournal(player);

        Assert.Contains("Active Quests", journal);
        Assert.Contains("Rat Problem", journal);
        Assert.Contains("kill_rats", journal.ToLowerInvariant());
    }

    [Fact]
    public void FormatJournal_EmptyWhenNoQuests()
    {
        var player = CreatePlayer();

        var journal = _engine.FormatJournal(player);

        Assert.Contains("empty", journal.ToLowerInvariant());
    }

    // ── Helpers ──

    private static PlayerCharacter CreatePlayer(int level = 3) => new()
    {
        Id = "test-player",
        Name = "Test Hero",
        Race = "Human",
        Class = "Warrior",
        Level = level,
        CurrentRoomId = "tavern",
        Hp = 12, MaxHp = 12,
        Mp = 4, MaxMp = 4,
        Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10,
        Gold = 100
    };

    private static void SeedTestQuests(ContentRegistryService registry)
    {
        // Simple single-stage quest
        registry.Quests.Register(new QuestDefinition
        {
            Id = "rat_problem",
            Name = "Rat Problem",
            Description = "Clear the cellar of rats.",
            GiverId = "innkeeper_mara",
            MinLevel = 1,
            Stages =
            [
                new QuestStage
                {
                    Id = "hunt_rats",
                    Name = "Kill the Rats",
                    Objectives =
                    [
                        new QuestObjective { Id = "kill_rats", Type = ObjectiveType.Kill, TargetId = "cellar_rat", RequiredCount = 3 }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 50, Gold = 10 }
        });

        // Two-stage quest
        registry.Quests.Register(new QuestDefinition
        {
            Id = "two_stage_quest",
            Name = "The Two Stage Quest",
            Description = "Find and deliver.",
            GiverId = "innkeeper_mara",
            MinLevel = 1,
            Stages =
            [
                new QuestStage
                {
                    Id = "stage_1",
                    Name = "Find the Thing",
                    NextStageId = "stage_2",
                    Objectives =
                    [
                        new QuestObjective { Id = "find_thing", Type = ObjectiveType.Collect, TargetId = "thing", RequiredCount = 1 }
                    ]
                },
                new QuestStage
                {
                    Id = "stage_2",
                    Name = "Deliver the Thing",
                    Objectives =
                    [
                        new QuestObjective { Id = "deliver_thing", Type = ObjectiveType.Deliver, TargetId = "thing", RequiredCount = 1 }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 100, Gold = 25 }
        });

        // Quest with prerequisite
        registry.Quests.Register(new QuestDefinition
        {
            Id = "korgas_test",
            Name = "Korga's Test",
            Description = "Prove your mettle.",
            GiverId = "blacksmith_korga",
            Prerequisites = ["rat_problem"],
            MinLevel = 1,
            Stages =
            [
                new QuestStage
                {
                    Id = "prove_yourself",
                    Name = "Prove Yourself",
                    Objectives =
                    [
                        new QuestObjective { Id = "custom_prove", Type = ObjectiveType.Custom, CustomCondition = "Show battle prowess" }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 75 }
        });

        // High level quest
        registry.Quests.Register(new QuestDefinition
        {
            Id = "high_level_quest",
            Name = "Epic Adventure",
            Description = "For experienced heroes only.",
            GiverId = "elder",
            MinLevel = 10,
            Stages =
            [
                new QuestStage
                {
                    Id = "s1",
                    Name = "Begin",
                    Objectives = [new QuestObjective { Id = "o1", Type = ObjectiveType.Discover, TargetId = "dungeon" }]
                }
            ],
            Rewards = new QuestReward { Xp = 500 }
        });

        // Quest with custom objective
        registry.Quests.Register(new QuestDefinition
        {
            Id = "custom_quest",
            Name = "Custom Quest",
            Description = "A quest with a custom objective.",
            GiverId = "elder",
            MinLevel = 1,
            Stages =
            [
                new QuestStage
                {
                    Id = "custom_stage",
                    Name = "Do the Thing",
                    Objectives =
                    [
                        new QuestObjective { Id = "custom_obj", Type = ObjectiveType.Custom, CustomCondition = "Do something cool" }
                    ]
                }
            ],
            Rewards = new QuestReward { Xp = 25 }
        });
    }
}
