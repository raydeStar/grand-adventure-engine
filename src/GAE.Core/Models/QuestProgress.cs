namespace GAE.Core.Models;

/// <summary>
/// Per-player progress tracking for a single quest. Stored in PlayerCharacter.QuestLog
/// and persisted through the existing journal/checkpoint system.
/// </summary>
public class QuestProgress
{
    /// <summary>References QuestDefinition.Id in the content registry.</summary>
    public string QuestId { get; set; } = string.Empty;

    /// <summary>The world where this quest was accepted and progresses.</summary>
    public string WorldId { get; set; } = WorldDefaults.DefaultWorldId;

    /// <summary>Current quest status.</summary>
    public QuestStatus Status { get; set; } = QuestStatus.Active;

    /// <summary>ID of the current stage being pursued.</summary>
    public string CurrentStageId { get; set; } = string.Empty;

    /// <summary>Per-objective progress within the current stage.</summary>
    public List<ObjectiveProgress> Objectives { get; set; } = [];

    /// <summary>When the quest was accepted.</summary>
    public DateTimeOffset AcceptedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the quest was completed, if applicable.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Shared party quest group ID when this progress is pooled with other players.</summary>
    public string? PartyQuestGroupId { get; set; }

    /// <summary>
    /// AI-generated quest description that enriches the authored skeleton.
    /// Set when the quest is offered/accepted via narrator conversation.
    /// </summary>
    public string? NarratorDescription { get; set; }

    /// <summary>
    /// AI-generated stage descriptions, keyed by stage ID.
    /// Populated as the player advances through stages.
    /// </summary>
    public Dictionary<string, string> StageNarrations { get; set; } = new();
}

/// <summary>Progress toward a single objective within a quest stage.</summary>
public class ObjectiveProgress
{
    /// <summary>References QuestObjective.Id.</summary>
    public string ObjectiveId { get; set; } = string.Empty;

    /// <summary>Current count toward RequiredCount.</summary>
    public int CurrentCount { get; set; }

    /// <summary>Whether this objective is complete.</summary>
    public bool IsComplete { get; set; }
}
