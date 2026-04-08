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
    public Task<string?> OnEnemyKilledAsync(PlayerCharacter player, string enemyId, string? enemyName = null, CancellationToken ct = default)
    {
        return ProcessObjectivesAsync(player, ObjectiveType.Kill, enemyId, enemyName, count: 1, currentRoomId: player.CurrentRoomId, ct: ct);
    }

    /// <summary>
    /// Called after an item is picked up. Updates Collect objectives matching the item ID or name.
    /// </summary>
    public Task<string?> OnItemCollectedAsync(PlayerCharacter player, string itemId, string? itemName = null, int count = 1, CancellationToken ct = default)
    {
        return ProcessObjectivesAsync(player, ObjectiveType.Collect, itemId, itemName, count, player.CurrentRoomId, ct);
    }

    /// <summary>
    /// Called when a player enters a room. Updates Discover objectives matching the room ID.
    /// </summary>
    public async Task<string?> OnRoomEnteredAsync(PlayerCharacter player, Room room, CancellationToken ct = default)
    {
        var updates = new List<string>();
        var roomId = NormalizeRoomId(room.Id);

        var discover = await ProcessObjectivesAsync(player, ObjectiveType.Discover, roomId, count: 1, currentRoomId: roomId, ct: ct);
        if (discover is not null)
            updates.Add(discover);

        foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
        {
            var quest = _registry.Quests.GetById(progress.QuestId);
            var stage = quest?.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
            if (stage is null)
                continue;

            foreach (var obj in stage.Objectives.Where(o => o.Type == ObjectiveType.Escort))
            {
                if (!string.Equals(obj.LocationConstraint, roomId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var escortNpcPresent = room.Npcs.Any(n => n.Id.Equals(obj.TargetId, StringComparison.OrdinalIgnoreCase)
                    && (!n.Hp.HasValue || n.Hp.Value > 0));
                if (!escortNpcPresent)
                    continue;

                if (await _questEngine.AdvanceObjectiveAsync(player, progress.QuestId, obj.Id, obj.RequiredCount, ct))
                    updates.Add($"Escort objective complete: {obj.Description ?? obj.TargetName ?? obj.Id}");
            }
        }

        return updates.Count > 0 ? string.Join("; ", updates) : null;
    }

    /// <summary>
    /// Called when a player initiates conversation with an NPC. Updates TalkTo objectives.
    /// Also checks for Deliver objectives if the player has the required items.
    /// </summary>
    public async Task<string?> OnConversationStartedAsync(PlayerCharacter player, Npc npc, CancellationToken ct = default)
    {
        var updates = new List<string>();

        // TalkTo objectives
        var talkUpdate = await ProcessObjectivesAsync(player, ObjectiveType.TalkTo, npc.Id, npc.Name, count: 1, currentRoomId: player.CurrentRoomId, ct: ct);
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

                InventoryItem? deliveredItem = null;
                if (!string.IsNullOrWhiteSpace(obj.RequiredItemId))
                {
                    deliveredItem = player.Inventory.FirstOrDefault(item =>
                        item.Id.Equals(obj.RequiredItemId, StringComparison.OrdinalIgnoreCase));
                    if (deliveredItem is null || deliveredItem.Quantity < obj.RequiredCount)
                        continue;
                }

                if (await _questEngine.AdvanceObjectiveAsync(player, progress.QuestId, obj.Id, ct: ct))
                {
                    if (deliveredItem is not null)
                    {
                        deliveredItem.Quantity -= obj.RequiredCount;
                        if (deliveredItem.Quantity <= 0)
                            player.Inventory.Remove(deliveredItem);
                    }

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
    public async Task<string?> OnCustomObjectivesCompletedAsync(PlayerCharacter player, List<string> completedObjectiveIds, CancellationToken ct = default)
    {
        if (completedObjectiveIds.Count == 0) return null;

        var updates = new List<string>();

        foreach (var objectiveId in completedObjectiveIds)
        {
            foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
            {
                if (await _questEngine.CompleteCustomObjectiveAsync(player, progress.QuestId, objectiveId, ct))
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
    public async Task<(string? Summary, QuestReward? Reward)> ProcessNarratorQuestUpdatesAsync(
        PlayerCharacter player, QuestUpdates updates, Npc? currentNpc = null, CancellationToken ct = default)
    {
        var summaries = new List<string>();
        QuestReward? reward = null;

        // Quest acceptance via narrator
        if (updates.AcceptedQuestId is not null)
        {
            var progress = await _questEngine.AcceptQuestAsync(player, updates.AcceptedQuestId, updates.QuestDescription, ct);
            if (progress is not null)
            {
                var quest = _registry.Quests.GetById(updates.AcceptedQuestId);
                summaries.Add($"Accepted quest: {quest?.Name ?? updates.AcceptedQuestId}");
            }
        }

        if (updates.DeclinedQuestId is not null)
        {
            var quest = _registry.Quests.GetById(updates.DeclinedQuestId);
            summaries.Add($"Declined quest: {quest?.Name ?? updates.DeclinedQuestId}");
        }

        foreach (var (questId, reason) in updates.FailureRecommended)
        {
            var quest = _registry.Quests.GetById(questId);
            if (quest is null || string.IsNullOrWhiteSpace(quest.FailureHint))
                continue;

            if (await _questEngine.FailQuestAsync(player, questId, reason, ct))
                summaries.Add($"Failed quest: {quest.Name}");
        }

        // Quest turn-in via narrator
        if (updates.TurnInQuestId is not null)
        {
            reward = await _questEngine.TurnInQuestAsync(player, updates.TurnInQuestId, currentNpc, ct);
            if (reward is not null)
            {
                var quest = _registry.Quests.GetById(updates.TurnInQuestId);
                summaries.Add($"Completed quest: {quest?.Name ?? updates.TurnInQuestId}");
            }
        }

        // Custom objective completions
        if (updates.CompletedCustomObjectives.Count > 0)
        {
            var customSummary = await OnCustomObjectivesCompletedAsync(player, updates.CompletedCustomObjectives, ct);
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

    /// <summary>
    /// Called after the player survives another combat round while a fight is still ongoing.
    /// </summary>
    public Task<string?> OnCombatRoundSurvivedAsync(PlayerCharacter player, string roomId, CancellationToken ct = default)
    {
        var normalizedRoomId = NormalizeRoomId(roomId);
        return ProcessObjectivesAsync(player, ObjectiveType.Survive, normalizedRoomId, count: 1, currentRoomId: normalizedRoomId, ct: ct);
    }

    private static string NormalizeRoomId(string roomId)
    {
        var separatorIndex = roomId.IndexOf(':');
        return separatorIndex >= 0 && separatorIndex < roomId.Length - 1
            ? roomId[(separatorIndex + 1)..]
            : roomId;
    }

    private async Task<string?> ProcessObjectivesAsync(
        PlayerCharacter player,
        ObjectiveType type,
        string targetId,
        string? targetName = null,
        int count = 1,
        string? currentRoomId = null,
        CancellationToken ct = default)
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
                if (!string.IsNullOrWhiteSpace(obj.LocationConstraint)
                    && !string.Equals(obj.LocationConstraint, currentRoomId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Match by ID or by name (case-insensitive)
                var matches = (!string.IsNullOrWhiteSpace(obj.TargetId)
                        && (obj.TargetId.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                            || (targetName is not null && obj.TargetId.Equals(targetName, StringComparison.OrdinalIgnoreCase))))
                    || (!string.IsNullOrWhiteSpace(obj.TargetName)
                        && targetName is not null
                        && obj.TargetName.Equals(targetName, StringComparison.OrdinalIgnoreCase));

                if (!matches) continue;

                var stageBeforeAdvance = progress.CurrentStageId;
                if (await _questEngine.AdvanceObjectiveAsync(player, progress.QuestId, obj.Id, count, ct))
                {
                    var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                    // If stage advanced, the old objective is no longer in the list — treat as complete
                    if (objProgress is null || objProgress.IsComplete || progress.CurrentStageId != stageBeforeAdvance)
                        updates.Add($"Objective complete: {obj.Description ?? obj.Id}");
                    else
                        updates.Add($"Quest progress: {obj.Description ?? obj.Id} ({objProgress.CurrentCount}/{obj.RequiredCount})");
                }
            }
        }

        return updates.Count > 0 ? string.Join("; ", updates) : null;
    }
}
