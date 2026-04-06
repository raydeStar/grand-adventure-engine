namespace GAE.Core.Models;

/// <summary>
/// Shared quest state for party quests. Multiple player quest logs can point at the same
/// group ID so objective progress is pooled and rewards resolve together.
/// </summary>
public class PartyQuestProgress
{
    public string GroupId { get; set; } = string.Empty;
    public string QuestId { get; set; } = string.Empty;
    public string WorldId { get; set; } = WorldDefaults.DefaultWorldId;
    public QuestStatus Status { get; set; } = QuestStatus.Active;
    public string CurrentStageId { get; set; } = string.Empty;
    public List<ObjectiveProgress> Objectives { get; set; } = [];
    public List<string> ParticipantPlayerIds { get; set; } = [];
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}