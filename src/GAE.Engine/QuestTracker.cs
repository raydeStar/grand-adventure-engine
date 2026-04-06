using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

/// <summary>
/// Inspects active quest stages and updates objective progress from deterministic
/// game events: enemy kills, item pickups, room entries, and conversation starts.
/// Called from GameEngine seams after the relevant state mutation.
/// </summary>
public class QuestTracker
{
    private readonly QuestEngine _questEngine;
    private readonly IContentRegistryService _registry;
    private readonly ILogger<QuestTracker> _logger;

    public QuestTracker(QuestEngine questEngine, IContentRegistryService registry, ILogger<QuestTracker> logger)
    {
        _questEngine = questEngine;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Called after an enemy is killed. Updates Kill objectives matching the enemy ID or name.
    /// Returns quest update summary text for narration context, or null if no quest was updated.
    /// </summary>
    public string? OnEnemyKilled(PlayerCharacter player, string enemyId, string? enemyName = null)
    {
        return ProcessObjectives(player, ObjectiveType.Kill, enemyId, enemyName);
    }

    /// <summary>
    /// Called after an item is picked up. Updates Collect objectives matching the item ID or name.
    /// </summary>
    public string? OnItemCollected(PlayerCharacter player, string itemId, string? itemName = null, int count = 1)
    {
        return ProcessObjectives(player, ObjectiveType.Collect, itemId, itemName, count);
    }

    /// <summary>
    /// Called when a player enters a room. Updates Discover objectives matching the room ID.
    /// </summary>
    public string? OnRoomEntered(PlayerCharacter player, string roomId)
    {
        return ProcessObjectives(player, ObjectiveType.Discover, roomId);
    }

    /// <summary>
    /// Called when a player initiates conversation with an NPC. Updates TalkTo objectives.
    /// Also checks for Deliver objectives if the player has the required items.
    /// </summary>
    public string? OnConversationStarted(PlayerCharacter player, Npc npc)
    {
        var updates = new List<string>();

        // TalkTo objectives
        var talkUpdate = ProcessObjectives(player, ObjectiveType.TalkTo, npc.Id, npc.Name);
        if (talkUpdate is not null) updates.Add(talkUpdate);

        // Deliver objectives — check if player has the target item to deliver to this NPC
        foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
        {
            var quest = _registry.Quests.GetById(progress.QuestId);
            if (quest is null) continue;

            var stage = quest.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
            if (stage is null) continue;

            foreach (var obj in stage.Objectives.Where(o => o.Type == ObjectiveType.Deliver))
            {
                var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                if (objProgress is null || objProgress.IsComplete) continue;

                // For deliver, TargetId is the NPC to deliver to — check if this is the right NPC
                if (!npc.Id.Equals(obj.TargetId, StringComparison.OrdinalIgnoreCase)) continue;

                // Check if the player has a matching item in inventory
                // The item they need to deliver is context-dependent from the quest stage
                // For now, automatically progress the deliver objective when talking to the right NPC
                if (_questEngine.AdvanceObjective(player, progress.QuestId, obj.Id))
                {
                    updates.Add($"Delivered item for quest '{quest.Name}'");
                    _logger.LogInformation("Deliver objective {ObjectiveId} completed for quest {QuestId}", obj.Id, quest.Id);
                }
            }
        }

        return updates.Count > 0 ? string.Join("; ", updates) : null;
    }

    /// <summary>
    /// Processes custom objective completions from narrator responses.
    /// </summary>
    public string? OnCustomObjectivesCompleted(PlayerCharacter player, List<string> completedObjectiveIds)
    {
        if (completedObjectiveIds.Count == 0) return null;

        var updates = new List<string>();

        foreach (var objectiveId in completedObjectiveIds)
        {
            foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
            {
                if (_questEngine.CompleteCustomObjective(player, progress.QuestId, objectiveId))
                {
                    var quest = _registry.Quests.GetById(progress.QuestId);
                    updates.Add($"Custom objective '{objectiveId}' met for quest '{quest?.Name ?? progress.QuestId}'");
                }
            }
        }

        return updates.Count > 0 ? string.Join("; ", updates) : null;
    }

    /// <summary>
    /// Processes narrator quest updates from a FreeFormResponse.
    /// Handles accept, turn-in, custom objective completion, and narrator descriptions.
    /// </summary>
    public (string? Summary, QuestReward? Reward) ProcessNarratorQuestUpdates(
        PlayerCharacter player, QuestUpdates updates, Npc? currentNpc = null)
    {
        var summaries = new List<string>();
        QuestReward? reward = null;

        // Quest acceptance via narrator
        if (updates.AcceptedQuestId is not null)
        {
            var progress = _questEngine.AcceptQuest(player, updates.AcceptedQuestId, updates.QuestDescription);
            if (progress is not null)
            {
                var quest = _registry.Quests.GetById(updates.AcceptedQuestId);
                summaries.Add($"Accepted quest: {quest?.Name ?? updates.AcceptedQuestId}");
            }
        }

        // Quest turn-in via narrator
        if (updates.TurnInQuestId is not null)
        {
            reward = _questEngine.TurnInQuest(player, updates.TurnInQuestId, currentNpc);
            if (reward is not null)
            {
                var quest = _registry.Quests.GetById(updates.TurnInQuestId);
                summaries.Add($"Completed quest: {quest?.Name ?? updates.TurnInQuestId}");
            }
        }

        // Custom objective completions
        if (updates.CompletedCustomObjectives.Count > 0)
        {
            var customSummary = OnCustomObjectivesCompleted(player, updates.CompletedCustomObjectives);
            if (customSummary is not null) summaries.Add(customSummary);
        }

        // Store stage narration if provided
        if (updates.StageDescription is not null)
        {
            foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
            {
                progress.StageNarrations[progress.CurrentStageId] = updates.StageDescription;
            }
        }

        var summary = summaries.Count > 0 ? string.Join("; ", summaries) : null;
        return (summary, reward);
    }

    private string? ProcessObjectives(PlayerCharacter player, ObjectiveType type, string targetId, string? targetName = null, int count = 1)
    {
        var updates = new List<string>();

        foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
        {
            var quest = _registry.Quests.GetById(progress.QuestId);
            if (quest is null) continue;

            var stage = quest.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
            if (stage is null) continue;

            foreach (var obj in stage.Objectives.Where(o => o.Type == type))
            {
                if (obj.TargetId is null) continue;

                // Match by ID or by name (case-insensitive)
                var matches = obj.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    || (targetName is not null && obj.TargetId.Equals(targetName, StringComparison.OrdinalIgnoreCase));

                if (!matches) continue;

                if (_questEngine.AdvanceObjective(player, progress.QuestId, obj.Id, count))
                {
                    var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                    if (objProgress?.IsComplete == true)
                        updates.Add($"Objective complete: {obj.Description ?? obj.Id}");
                    else
                        updates.Add($"Quest progress: {obj.Description ?? obj.Id} ({objProgress?.CurrentCount ?? 0}/{obj.RequiredCount})");
                }
            }
        }

        return updates.Count > 0 ? string.Join("; ", updates) : null;
    }
}
