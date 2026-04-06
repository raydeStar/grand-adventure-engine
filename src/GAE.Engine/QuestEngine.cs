using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

/// <summary>
/// Core quest lifecycle service. Handles accept, advance, complete, abandon,
/// prerequisite checks, reward application, and turn-in validation.
/// Mutates PlayerCharacter.QuestLog directly — persistence is through existing SavePlayerAsync.
/// </summary>
public class QuestEngine
{
    private readonly IContentRegistryService _registry;
    private readonly ILogger<QuestEngine> _logger;

    public QuestEngine(IContentRegistryService registry, ILogger<QuestEngine> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Checks whether a player can accept a specific quest.</summary>
    public (bool CanAccept, string? Reason) CanAcceptQuest(PlayerCharacter player, string questId)
    {
        var quest = _registry.Quests.GetById(questId);
        if (quest is null)
            return (false, "Quest not found.");

        if (player.QuestLog.Any(q => q.QuestId == questId && q.Status == QuestStatus.Active))
            return (false, "Quest already active.");

        if (quest.IsOneTime && player.QuestLog.Any(q => q.QuestId == questId && q.Status == QuestStatus.Completed))
            return (false, "Quest already completed.");

        if (player.Level < quest.MinLevel)
            return (false, $"Requires level {quest.MinLevel}.");

        foreach (var prereq in quest.Prerequisites)
        {
            if (!player.QuestLog.Any(q => q.QuestId == prereq && q.Status == QuestStatus.Completed))
                return (false, $"Requires completing '{_registry.Quests.GetById(prereq)?.Name ?? prereq}' first.");
        }

        if (quest.IsPartyQuest)
            return (false, "Party quests are not yet supported.");

        return (true, null);
    }

    /// <summary>Accepts a quest and adds it to the player's quest log.</summary>
    public QuestProgress? AcceptQuest(PlayerCharacter player, string questId, string? narratorDescription = null)
    {
        var (canAccept, reason) = CanAcceptQuest(player, questId);
        if (!canAccept)
        {
            _logger.LogWarning("Player {PlayerId} cannot accept quest {QuestId}: {Reason}", player.Id, questId, reason);
            return null;
        }

        var quest = _registry.Quests.GetById(questId)!;
        var firstStage = quest.Stages.FirstOrDefault();
        if (firstStage is null)
        {
            _logger.LogError("Quest {QuestId} has no stages", questId);
            return null;
        }

        var progress = new QuestProgress
        {
            QuestId = questId,
            Status = QuestStatus.Active,
            CurrentStageId = firstStage.Id,
            NarratorDescription = narratorDescription ?? quest.Description,
            Objectives = firstStage.Objectives.Select(o => new ObjectiveProgress
            {
                ObjectiveId = o.Id,
                CurrentCount = 0,
                IsComplete = false
            }).ToList()
        };

        player.QuestLog.Add(progress);
        _logger.LogInformation("Player {PlayerId} accepted quest {QuestId}", player.Id, questId);
        return progress;
    }

    /// <summary>Abandons an active quest.</summary>
    public bool AbandonQuest(PlayerCharacter player, string questId)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null) return false;

        progress.Status = QuestStatus.Abandoned;
        _logger.LogInformation("Player {PlayerId} abandoned quest {QuestId}", player.Id, questId);
        return true;
    }

    /// <summary>
    /// Advances a quest objective by count. Returns true if the objective was updated.
    /// Automatically advances stages and marks quest ready for turn-in when appropriate.
    /// </summary>
    public bool AdvanceObjective(PlayerCharacter player, string questId, string objectiveId, int count = 1)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null) return false;

        var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == objectiveId);
        if (objProgress is null || objProgress.IsComplete) return false;

        var quest = _registry.Quests.GetById(questId);
        if (quest is null) return false;

        var stage = quest.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
        var objective = stage?.Objectives.FirstOrDefault(o => o.Id == objectiveId);
        if (objective is null) return false;

        objProgress.CurrentCount = Math.Min(objProgress.CurrentCount + count, objective.RequiredCount);
        objProgress.IsComplete = objProgress.CurrentCount >= objective.RequiredCount;

        _logger.LogDebug("Quest {QuestId} objective {ObjectiveId}: {Current}/{Required}",
            questId, objectiveId, objProgress.CurrentCount, objective.RequiredCount);

        // Check if all objectives in the current stage are complete
        if (progress.Objectives.All(o => o.IsComplete))
        {
            TryAdvanceStage(player, progress, quest);
        }

        return true;
    }

    /// <summary>Marks a custom objective as complete.</summary>
    public bool CompleteCustomObjective(PlayerCharacter player, string questId, string objectiveId)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null) return false;

        var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == objectiveId);
        if (objProgress is null || objProgress.IsComplete) return false;

        objProgress.CurrentCount = 1;
        objProgress.IsComplete = true;

        _logger.LogInformation("Custom objective {ObjectiveId} completed for quest {QuestId}", objectiveId, questId);

        // Check if all objectives in the current stage are complete
        var quest = _registry.Quests.GetById(questId);
        if (quest is not null && progress.Objectives.All(o => o.IsComplete))
        {
            TryAdvanceStage(player, progress, quest);
        }

        return true;
    }

    /// <summary>
    /// Attempts to complete and turn in a quest. Returns rewards if successful.
    /// </summary>
    public QuestReward? TurnInQuest(PlayerCharacter player, string questId, Npc? turnInNpc = null)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.ReadyToTurnIn);
        if (progress is null) return null;

        var quest = _registry.Quests.GetById(questId);
        if (quest is null) return null;

        // Validate turn-in NPC if specified
        var expectedTurnInNpc = quest.TurnInNpcId ?? quest.GiverId;
        if (turnInNpc is not null && !turnInNpc.Id.Equals(expectedTurnInNpc, StringComparison.OrdinalIgnoreCase))
            return null;

        progress.Status = QuestStatus.Completed;
        progress.CompletedAt = DateTimeOffset.UtcNow;

        // Apply rewards
        ApplyRewards(player, quest.Rewards);

        _logger.LogInformation("Player {PlayerId} completed quest {QuestId}, rewards: {Xp}xp {Gold}g",
            player.Id, questId, quest.Rewards.Xp, quest.Rewards.Gold);

        return quest.Rewards;
    }

    /// <summary>Gets quests available to a player from a specific NPC.</summary>
    public IReadOnlyList<QuestDefinition> GetAvailableQuests(PlayerCharacter player, Npc npc)
    {
        var available = new List<QuestDefinition>();
        foreach (var questId in npc.QuestsOffered)
        {
            var (canAccept, _) = CanAcceptQuest(player, questId);
            if (canAccept)
            {
                var quest = _registry.Quests.GetById(questId);
                if (quest is not null)
                    available.Add(quest);
            }
        }
        return available;
    }

    /// <summary>Gets quests that can be turned in to a specific NPC.</summary>
    public IReadOnlyList<QuestProgress> GetTurnInableQuests(PlayerCharacter player, Npc npc)
    {
        return player.QuestLog
            .Where(q => q.Status == QuestStatus.ReadyToTurnIn)
            .Where(q =>
            {
                var quest = _registry.Quests.GetById(q.QuestId);
                if (quest is null) return false;
                var turnInNpc = quest.TurnInNpcId ?? quest.GiverId;
                return turnInNpc.Equals(npc.Id, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    /// <summary>Formats the player's quest journal for display.</summary>
    public string FormatJournal(PlayerCharacter player)
    {
        var active = player.QuestLog.Where(q => q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn).ToList();
        var completed = player.QuestLog.Where(q => q.Status == QuestStatus.Completed).ToList();

        if (active.Count == 0 && completed.Count == 0)
            return "Your quest journal is empty. Speak with the townsfolk — someone may need your help.";

        var sb = new System.Text.StringBuilder();

        if (active.Count > 0)
        {
            sb.AppendLine("📜 **Active Quests**");
            foreach (var q in active)
            {
                var quest = _registry.Quests.GetById(q.QuestId);
                var name = quest?.Name ?? q.QuestId;
                var status = q.Status == QuestStatus.ReadyToTurnIn ? " ✅ READY TO TURN IN" : "";
                sb.AppendLine($"  **{name}**{status}");
                sb.AppendLine($"  {q.NarratorDescription ?? quest?.Description ?? "No description."}");

                // Show objective progress
                if (quest is not null)
                {
                    var stage = quest.Stages.FirstOrDefault(s => s.Id == q.CurrentStageId);
                    if (stage is not null)
                    {
                        foreach (var obj in stage.Objectives)
                        {
                            var prog = q.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                            var check = prog?.IsComplete == true ? "☑" : "☐";
                            var countText = obj.RequiredCount > 1 ? $" ({prog?.CurrentCount ?? 0}/{obj.RequiredCount})" : "";
                            sb.AppendLine($"    {check} {obj.Description ?? obj.Id}{countText}");
                        }
                    }
                }
                sb.AppendLine();
            }
        }

        if (completed.Count > 0)
        {
            sb.AppendLine($"📕 **Completed Quests** ({completed.Count})");
            foreach (var q in completed)
            {
                var quest = _registry.Quests.GetById(q.QuestId);
                sb.AppendLine($"  ✅ {quest?.Name ?? q.QuestId}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats details for a specific quest.</summary>
    public string FormatQuestInfo(PlayerCharacter player, string questNameOrId)
    {
        // Find by ID first, then by name
        var progress = player.QuestLog.FirstOrDefault(q =>
            q.QuestId.Equals(questNameOrId, StringComparison.OrdinalIgnoreCase));

        if (progress is null)
        {
            // Try matching by quest name
            foreach (var q in player.QuestLog)
            {
                var def = _registry.Quests.GetById(q.QuestId);
                if (def?.Name.Contains(questNameOrId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    progress = q;
                    break;
                }
            }
        }

        if (progress is null)
            return $"No quest matching '{questNameOrId}' found in your journal.";

        var quest = _registry.Quests.GetById(progress.QuestId);
        if (quest is null)
            return "Quest definition not found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📜 **{quest.Name}**");
        sb.AppendLine($"Status: {progress.Status}");
        sb.AppendLine($"{progress.NarratorDescription ?? quest.Description}");
        sb.AppendLine();

        // Show all stages with progress
        foreach (var stage in quest.Stages)
        {
            var isCurrent = stage.Id == progress.CurrentStageId;
            var prefix = isCurrent ? "▶" : (progress.Status == QuestStatus.Completed ? "✅" : "○");
            sb.AppendLine($"{prefix} **{stage.Name}**");

            if (progress.StageNarrations.TryGetValue(stage.Id, out var stageNarration))
                sb.AppendLine($"  {stageNarration}");

            if (isCurrent || progress.Status == QuestStatus.Completed)
            {
                foreach (var obj in stage.Objectives)
                {
                    var prog = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                    var check = prog?.IsComplete == true ? "☑" : "☐";
                    var countText = obj.RequiredCount > 1 ? $" ({prog?.CurrentCount ?? 0}/{obj.RequiredCount})" : "";
                    sb.AppendLine($"    {check} {obj.Description ?? obj.Id}{countText}");
                }
            }
        }

        if (quest.Rewards.Xp > 0 || quest.Rewards.Gold > 0 || quest.Rewards.Items.Count > 0)
        {
            sb.AppendLine();
            sb.Append("**Rewards:** ");
            var parts = new List<string>();
            if (quest.Rewards.Xp > 0) parts.Add($"{quest.Rewards.Xp} XP");
            if (quest.Rewards.Gold > 0) parts.Add($"{quest.Rewards.Gold} gold");
            foreach (var item in quest.Rewards.Items)
            {
                var template = _registry.Items.GetById(item.ItemId);
                parts.Add($"{template?.Name ?? item.ItemId} x{item.Quantity}");
            }
            sb.AppendLine(string.Join(", ", parts));
        }

        return sb.ToString().TrimEnd();
    }

    private void TryAdvanceStage(PlayerCharacter player, QuestProgress progress, QuestDefinition quest)
    {
        var currentStage = quest.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
        if (currentStage is null) return;

        if (currentStage.NextStageId is null)
        {
            // Final stage complete — quest is ready for turn-in
            progress.Status = QuestStatus.ReadyToTurnIn;
            _logger.LogInformation("Quest {QuestId} ready for turn-in for player {PlayerId}", quest.Id, player.Id);
            return;
        }

        // Advance to next stage
        var nextStage = quest.Stages.FirstOrDefault(s => s.Id == currentStage.NextStageId);
        if (nextStage is null)
        {
            _logger.LogError("Quest {QuestId} stage {StageId} references missing next stage {NextStageId}",
                quest.Id, currentStage.Id, currentStage.NextStageId);
            return;
        }

        progress.CurrentStageId = nextStage.Id;
        progress.Objectives = nextStage.Objectives.Select(o => new ObjectiveProgress
        {
            ObjectiveId = o.Id,
            CurrentCount = 0,
            IsComplete = false
        }).ToList();

        _logger.LogInformation("Quest {QuestId} advanced to stage {StageId} for player {PlayerId}",
            quest.Id, nextStage.Id, player.Id);
    }

    private void ApplyRewards(PlayerCharacter player, QuestReward rewards)
    {
        if (rewards.Xp > 0) player.Xp += rewards.Xp;
        if (rewards.Gold > 0) player.Gold += rewards.Gold;

        foreach (var rewardItem in rewards.Items)
        {
            var template = _registry.Items.GetById(rewardItem.ItemId);
            if (template is not null)
            {
                player.Inventory.Add(template.ToInventoryItem(rewardItem.Quantity));
            }
            else
            {
                _logger.LogWarning("Quest reward item '{ItemId}' not found in registry", rewardItem.ItemId);
            }
        }
    }
}
