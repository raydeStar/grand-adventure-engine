using GAE.Core.Interfaces;
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
    private readonly IStateManager? _stateManager;

    public QuestEngine(IContentRegistryService registry, ILogger<QuestEngine> logger, IStateManager? stateManager = null)
    {
        _registry = registry;
        _logger = logger;
        _stateManager = stateManager;
    }

    /// <summary>Checks whether a player can accept a specific quest.</summary>
    public (bool CanAccept, string? Reason) CanAcceptQuest(PlayerCharacter player, string questId, Npc? questNpc = null)
    {
        var quest = _registry.Quests.GetById(questId);
        if (quest is null)
            return (false, "Quest not found.");

        if (player.QuestLog.Any(q => q.QuestId == questId && (q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn)))
            return (false, "Quest already active.");

        if (!quest.IsRepeatable && player.QuestLog.Any(q => q.QuestId == questId && q.Status == QuestStatus.Completed))
            return (false, "Quest already completed.");

        if (player.Level < quest.MinLevel)
            return (false, $"Requires level {quest.MinLevel}.");

        if (!string.IsNullOrWhiteSpace(quest.RequiredFaction)
            && !string.Equals(player.Faction, quest.RequiredFaction, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Requires faction '{quest.RequiredFaction}'.");
        }

        var unlockingQuests = _registry.Quests.GetAll()
            .Where(q => string.Equals(q.Rewards.UnlockQuestId, questId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (unlockingQuests.Count > 0
            && !player.QuestLog.Any(q => q.Status == QuestStatus.Completed
                && unlockingQuests.Any(unlocker => unlocker.Id.Equals(q.QuestId, StringComparison.OrdinalIgnoreCase))))
        {
            var unlockNames = string.Join(", ", unlockingQuests.Select(q => q.Name));
            return (false, $"Requires unlocking via '{unlockNames}'.");
        }

        foreach (var prereq in quest.Prerequisites)
        {
            if (!player.QuestLog.Any(q => q.QuestId == prereq && q.Status == QuestStatus.Completed))
                return (false, $"Requires completing '{_registry.Quests.GetById(prereq)?.Name ?? prereq}' first.");
        }

        if (questNpc is not null)
        {
            if (!string.Equals(questNpc.Id, quest.GiverId, StringComparison.OrdinalIgnoreCase))
                return (false, "This quest must be accepted from its giver.");

            if (!string.IsNullOrWhiteSpace(quest.QuestGiverRoomId)
                && !string.Equals(player.CurrentRoomId, quest.QuestGiverRoomId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "You must speak to the quest giver in the proper location.");
            }

            if (quest.MinDisposition.HasValue && questNpc.DispositionState.Intensity < quest.MinDisposition.Value)
                return (false, $"{questNpc.Name} does not trust you enough yet.");
        }

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

        var progress = CreateQuestProgress(quest, firstStage, narratorDescription, player.ActiveWorldId, partyQuestGroupId: null);

        player.QuestLog.Add(progress);
        _logger.LogInformation("Player {PlayerId} accepted quest {QuestId}", player.Id, questId);
        return progress;
    }

    /// <summary>Accepts a quest, creating or joining a shared party quest group when necessary.</summary>
    public async Task<QuestProgress?> AcceptQuestAsync(PlayerCharacter player, string questId, string? narratorDescription = null, CancellationToken ct = default)
    {
        var quest = _registry.Quests.GetById(questId);
        if (quest is null)
            return null;

        if (!quest.IsPartyQuest || _stateManager is null)
            return AcceptQuest(player, questId, narratorDescription);

        var (canAccept, reason) = CanAcceptQuest(player, questId);
        if (!canAccept)
        {
            _logger.LogWarning("Player {PlayerId} cannot accept party quest {QuestId}: {Reason}", player.Id, questId, reason);
            return null;
        }

        var firstStage = quest.Stages.FirstOrDefault();
        if (firstStage is null)
            return null;

        var partyState = await FindJoinablePartyQuestAsync(player, questId, firstStage, ct);
        if (!partyState.ParticipantPlayerIds.Contains(player.Id, StringComparer.OrdinalIgnoreCase))
            partyState.ParticipantPlayerIds.Add(player.Id);

        var progress = CreateQuestProgressFromParty(quest, partyState, narratorDescription);
        player.QuestLog.Add(progress);
        await _stateManager.SavePartyQuestAsync(partyState, ct);

        _logger.LogInformation("Player {PlayerId} joined party quest {QuestId} in group {GroupId}", player.Id, questId, partyState.GroupId);
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

    /// <summary>Abandons a quest and detaches the player from party quest state if present.</summary>
    public async Task<bool> AbandonQuestAsync(PlayerCharacter player, string questId, CancellationToken ct = default)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null)
            return false;

        var abandoned = AbandonQuest(player, questId);
        if (!abandoned || string.IsNullOrWhiteSpace(progress.PartyQuestGroupId) || _stateManager is null)
            return abandoned;

        var party = await _stateManager.GetPartyQuestAsync(progress.PartyQuestGroupId, ct);
        if (party is null)
            return true;

        party.ParticipantPlayerIds.RemoveAll(id => id.Equals(player.Id, StringComparison.OrdinalIgnoreCase));
        if (party.ParticipantPlayerIds.Count == 0)
        {
            await _stateManager.RemovePartyQuestAsync(party.GroupId, ct);
        }
        else
        {
            await _stateManager.SavePartyQuestAsync(party, ct);
        }

        return true;
    }

    /// <summary>Marks an active quest as failed.</summary>
    public bool FailQuest(PlayerCharacter player, string questId, string? reason = null)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && (q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn));
        if (progress is null)
            return false;

        progress.Status = QuestStatus.Failed;
        progress.CompletedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(reason))
            progress.StageNarrations[progress.CurrentStageId] = reason;

        _logger.LogInformation("Player {PlayerId} failed quest {QuestId}: {Reason}", player.Id, questId, reason ?? "no reason provided");
        return true;
    }

    /// <summary>Fails a quest, propagating the status across a party quest group when applicable.</summary>
    public async Task<bool> FailQuestAsync(PlayerCharacter player, string questId, string? reason = null, CancellationToken ct = default)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && (q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn));
        if (progress is null)
            return false;

        if (string.IsNullOrWhiteSpace(progress.PartyQuestGroupId) || _stateManager is null)
            return FailQuest(player, questId, reason);

        var party = await _stateManager.GetPartyQuestAsync(progress.PartyQuestGroupId, ct);
        if (party is null)
            return false;

        party.Status = QuestStatus.Failed;
        party.CompletedAt = DateTimeOffset.UtcNow;
        await SyncPartyQuestToParticipantsAsync(party, ct);
        await _stateManager.SavePartyQuestAsync(party, ct);

        var players = await _stateManager.GetAllPlayersAsync(ct);
        foreach (var participantId in party.ParticipantPlayerIds)
        {
            var participant = players.FirstOrDefault(p => p.Id == participantId);
            var participantProgress = participant?.QuestLog.FirstOrDefault(q => q.PartyQuestGroupId == party.GroupId);
            if (participantProgress is null)
                continue;

            participantProgress.Status = QuestStatus.Failed;
            participantProgress.CompletedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(reason))
                participantProgress.StageNarrations[participantProgress.CurrentStageId] = reason;
            await _stateManager.SavePlayerAsync(participant!, ct);
        }

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
        if (stage is null || objective is null) return false;

        objProgress.CurrentCount = Math.Min(objProgress.CurrentCount + count, objective.RequiredCount);
        objProgress.IsComplete = objProgress.CurrentCount >= objective.RequiredCount;

        _logger.LogDebug("Quest {QuestId} objective {ObjectiveId}: {Current}/{Required}",
            questId, objectiveId, objProgress.CurrentCount, objective.RequiredCount);

        // Check if the current stage is complete
        if (IsStageComplete(stage, progress.Objectives))
        {
            TryAdvanceStage(player, progress, quest);
        }

        return true;
    }

    /// <summary>
    /// Advances a quest objective, pooling progress through shared party quest state when needed.
    /// </summary>
    public async Task<bool> AdvanceObjectiveAsync(PlayerCharacter player, string questId, string objectiveId, int count = 1, CancellationToken ct = default)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null || string.IsNullOrWhiteSpace(progress.PartyQuestGroupId) || _stateManager is null)
            return AdvanceObjective(player, questId, objectiveId, count);

        var party = await _stateManager.GetPartyQuestAsync(progress.PartyQuestGroupId, ct);
        var quest = _registry.Quests.GetById(questId);
        if (party is null || quest is null)
            return false;

        var stage = quest.Stages.FirstOrDefault(s => s.Id == party.CurrentStageId);
        var objective = stage?.Objectives.FirstOrDefault(o => o.Id == objectiveId);
        var objProgress = party.Objectives.FirstOrDefault(o => o.ObjectiveId == objectiveId);
        if (stage is null || objective is null || objProgress is null || objProgress.IsComplete)
            return false;

        objProgress.CurrentCount = Math.Min(objProgress.CurrentCount + count, objective.RequiredCount);
        objProgress.IsComplete = objProgress.CurrentCount >= objective.RequiredCount;

        if (IsStageComplete(stage, party.Objectives))
            TryAdvanceStage(party, quest);

        await SyncPartyQuestToParticipantsAsync(party, ct);
        await _stateManager.SavePartyQuestAsync(party, ct);
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

        // Check if the current stage is complete
        var quest = _registry.Quests.GetById(questId);
        var stage = quest?.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
        if (quest is not null && stage is not null && IsStageComplete(stage, progress.Objectives))
        {
            TryAdvanceStage(player, progress, quest);
        }

        return true;
    }

    /// <summary>Marks a custom objective complete, honoring shared party quest state when needed.</summary>
    public async Task<bool> CompleteCustomObjectiveAsync(PlayerCharacter player, string questId, string objectiveId, CancellationToken ct = default)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.Active);
        if (progress is null || string.IsNullOrWhiteSpace(progress.PartyQuestGroupId) || _stateManager is null)
            return CompleteCustomObjective(player, questId, objectiveId);

        var party = await _stateManager.GetPartyQuestAsync(progress.PartyQuestGroupId, ct);
        var quest = _registry.Quests.GetById(questId);
        if (party is null || quest is null)
            return false;

        var stage = quest.Stages.FirstOrDefault(s => s.Id == party.CurrentStageId);
        var objProgress = party.Objectives.FirstOrDefault(o => o.ObjectiveId == objectiveId);
        if (stage is null || objProgress is null || objProgress.IsComplete)
            return false;

        objProgress.CurrentCount = 1;
        objProgress.IsComplete = true;

        if (IsStageComplete(stage, party.Objectives))
            TryAdvanceStage(party, quest);

        await SyncPartyQuestToParticipantsAsync(party, ct);
        await _stateManager.SavePartyQuestAsync(party, ct);
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

    /// <summary>
    /// Completes and turns in a quest, applying shared rewards for party quests and NPC side-effects.
    /// </summary>
    public async Task<QuestReward?> TurnInQuestAsync(PlayerCharacter player, string questId, Npc? turnInNpc = null, CancellationToken ct = default)
    {
        var progress = player.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.Status == QuestStatus.ReadyToTurnIn);
        if (progress is null)
            return null;

        if (string.IsNullOrWhiteSpace(progress.PartyQuestGroupId) || _stateManager is null)
        {
            var reward = TurnInQuest(player, questId, turnInNpc);
            if (reward is not null)
                await ApplyNpcQuestRewardsAsync(player, questId, reward, turnInNpc, ct);
            return reward;
        }

        var party = await _stateManager.GetPartyQuestAsync(progress.PartyQuestGroupId, ct);
        var quest = _registry.Quests.GetById(questId);
        if (party is null || quest is null)
            return null;

        var expectedTurnInNpc = quest.TurnInNpcId ?? quest.GiverId;
        if (turnInNpc is not null && !turnInNpc.Id.Equals(expectedTurnInNpc, StringComparison.OrdinalIgnoreCase))
            return null;

        var players = await _stateManager.GetAllPlayersAsync(ct);
        foreach (var participantId in party.ParticipantPlayerIds)
        {
            var participant = players.FirstOrDefault(p => p.Id == participantId);
            if (participant is null)
                continue;

            var participantProgress = participant.QuestLog.FirstOrDefault(q => q.QuestId == questId && q.PartyQuestGroupId == party.GroupId);
            if (participantProgress is null)
                continue;

            participantProgress.Status = QuestStatus.Completed;
            participantProgress.CompletedAt = DateTimeOffset.UtcNow;
            participantProgress.CurrentStageId = party.CurrentStageId;
            participantProgress.Objectives = CloneObjectives(party.Objectives);
            ApplyRewards(participant, quest.Rewards);
            await _stateManager.SavePlayerAsync(participant, ct);
        }

        party.Status = QuestStatus.Completed;
        party.CompletedAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePartyQuestAsync(party, ct);
        await ApplyNpcQuestRewardsAsync(player, questId, quest.Rewards, turnInNpc, ct);

        _logger.LogInformation("Player {PlayerId} completed party quest {QuestId} for group {GroupId}", player.Id, questId, party.GroupId);
        return quest.Rewards;
    }

    /// <summary>Gets quests available to a player from a specific NPC.</summary>
    public IReadOnlyList<QuestDefinition> GetAvailableQuests(PlayerCharacter player, Npc npc)
    {
        var available = new List<QuestDefinition>();
        foreach (var questId in npc.QuestsOffered)
        {
            var quest = _registry.Quests.GetById(questId);
            if (quest is null)
                continue;

            if (!string.IsNullOrWhiteSpace(quest.QuestGiverRoomId)
                && !string.Equals(player.CurrentRoomId, quest.QuestGiverRoomId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (canAccept, _) = CanAcceptQuest(player, questId, npc);
            if (canAccept)
            {
                available.Add(quest);
            }
        }
        return available.OrderByDescending(q => npc.QuestGiverPriority).ThenBy(q => q.Name).ToList();
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

    private void TryAdvanceStage(PartyQuestProgress progress, QuestDefinition quest)
    {
        var currentStage = quest.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
        if (currentStage is null)
            return;

        if (currentStage.NextStageId is null)
        {
            progress.Status = QuestStatus.ReadyToTurnIn;
            return;
        }

        var nextStage = quest.Stages.FirstOrDefault(s => s.Id == currentStage.NextStageId);
        if (nextStage is null)
            return;

        progress.CurrentStageId = nextStage.Id;
        progress.Objectives = CreateObjectiveProgressList(nextStage);
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

    private static QuestProgress CreateQuestProgress(QuestDefinition quest, QuestStage stage, string? narratorDescription, string worldId, string? partyQuestGroupId) => new()
    {
        QuestId = quest.Id,
        WorldId = worldId,
        Status = QuestStatus.Active,
        CurrentStageId = stage.Id,
        NarratorDescription = narratorDescription ?? quest.Description,
        PartyQuestGroupId = partyQuestGroupId,
        Objectives = CreateObjectiveProgressList(stage)
    };

    private static QuestProgress CreateQuestProgressFromParty(QuestDefinition quest, PartyQuestProgress party, string? narratorDescription) => new()
    {
        QuestId = quest.Id,
        WorldId = party.WorldId,
        Status = party.Status,
        CurrentStageId = party.CurrentStageId,
        NarratorDescription = narratorDescription ?? quest.Description,
        PartyQuestGroupId = party.GroupId,
        AcceptedAt = party.AcceptedAt,
        CompletedAt = party.CompletedAt,
        Objectives = CloneObjectives(party.Objectives)
    };

    private static List<ObjectiveProgress> CreateObjectiveProgressList(QuestStage stage) =>
        stage.Objectives.Select(o => new ObjectiveProgress
        {
            ObjectiveId = o.Id,
            CurrentCount = 0,
            IsComplete = false
        }).ToList();

    private static List<ObjectiveProgress> CloneObjectives(IEnumerable<ObjectiveProgress> source) =>
        source.Select(o => new ObjectiveProgress
        {
            ObjectiveId = o.ObjectiveId,
            CurrentCount = o.CurrentCount,
            IsComplete = o.IsComplete
        }).ToList();

    private static bool IsStageComplete(QuestStage stage, IReadOnlyCollection<ObjectiveProgress> objectives)
    {
        if (objectives.Count == 0)
            return false;

        return stage.RequireAllObjectives
            ? objectives.All(o => o.IsComplete)
            : objectives.Any(o => o.IsComplete);
    }

    private async Task<PartyQuestProgress> FindJoinablePartyQuestAsync(PlayerCharacter player, string questId, QuestStage firstStage, CancellationToken ct)
    {
        var players = await _stateManager!.GetAllPlayersAsync(ct);
        foreach (var other in players)
        {
            if (other.Id == player.Id
                || !string.Equals(other.CurrentRoomId, player.CurrentRoomId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(other.ActiveWorldId, player.ActiveWorldId, StringComparison.OrdinalIgnoreCase))
                continue;

            var existing = other.QuestLog.FirstOrDefault(q => q.QuestId == questId && !string.IsNullOrWhiteSpace(q.PartyQuestGroupId)
                && (q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn));
            if (existing?.PartyQuestGroupId is null)
                continue;

            var party = await _stateManager.GetPartyQuestAsync(existing.PartyQuestGroupId, ct);
            if (party is not null)
                return party;
        }

        return new PartyQuestProgress
        {
            GroupId = Guid.NewGuid().ToString("N"),
            QuestId = questId,
            WorldId = player.ActiveWorldId,
            Status = QuestStatus.Active,
            CurrentStageId = firstStage.Id,
            Objectives = CreateObjectiveProgressList(firstStage),
            ParticipantPlayerIds = [player.Id],
            AcceptedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task SyncPartyQuestToParticipantsAsync(PartyQuestProgress party, CancellationToken ct)
    {
        var players = await _stateManager!.GetAllPlayersAsync(ct);
        foreach (var participantId in party.ParticipantPlayerIds)
        {
            var participant = players.FirstOrDefault(p => p.Id == participantId);
            if (participant is null)
                continue;

            var local = participant.QuestLog.FirstOrDefault(q => q.PartyQuestGroupId == party.GroupId);
            if (local is null)
                continue;

            local.Status = party.Status;
            local.CurrentStageId = party.CurrentStageId;
            local.CompletedAt = party.CompletedAt;
            local.Objectives = CloneObjectives(party.Objectives);
            await _stateManager.SavePlayerAsync(participant, ct);
        }
    }

    private async Task ApplyNpcQuestRewardsAsync(PlayerCharacter player, string questId, QuestReward rewards, Npc? turnInNpc, CancellationToken ct)
    {
        if (_stateManager is null)
            return;

        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        var quest = _registry.Quests.GetById(questId);
        if (room is null || quest is null)
            return;

        var fallbackNpcId = turnInNpc?.Id ?? quest.TurnInNpcId ?? quest.GiverId;
        bool roomChanged = false;

        foreach (var npc in room.Npcs)
        {
            int shift = 0;
            if (rewards.DispositionShifts.TryGetValue(npc.Id, out var targetedShift))
                shift += targetedShift;
            else if (!string.IsNullOrWhiteSpace(fallbackNpcId) && npc.Id.Equals(fallbackNpcId, StringComparison.OrdinalIgnoreCase))
                shift += rewards.DispositionShift;

            if (shift != 0)
            {
                npc.DispositionState.Intensity = Math.Clamp(npc.DispositionState.Intensity + shift, 0, 100);
                npc.DispositionState.LastUpdated = DateTimeOffset.UtcNow;
                npc.Disposition = npc.DispositionState.ToFlatDisposition();
                roomChanged = true;
            }

            if (rewards.NpcMemoryFlags.TryGetValue(npc.Id, out var targetedFlag)
                && !string.IsNullOrWhiteSpace(targetedFlag)
                && !npc.DispositionState.MemoryFlags.Contains(targetedFlag, StringComparer.OrdinalIgnoreCase))
            {
                npc.DispositionState.MemoryFlags.Add(targetedFlag);
                roomChanged = true;
            }

            if (!string.IsNullOrWhiteSpace(fallbackNpcId)
                && npc.Id.Equals(fallbackNpcId, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var legacyFlag in rewards.MemoryFlags)
                {
                    if (!string.IsNullOrWhiteSpace(legacyFlag)
                        && !npc.DispositionState.MemoryFlags.Contains(legacyFlag, StringComparer.OrdinalIgnoreCase))
                    {
                        npc.DispositionState.MemoryFlags.Add(legacyFlag);
                        roomChanged = true;
                    }
                }
            }
        }

        if (roomChanged)
            await _stateManager.SaveRoomAsync(room, ct);
    }
}
