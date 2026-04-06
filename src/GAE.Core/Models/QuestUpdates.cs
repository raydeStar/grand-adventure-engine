namespace GAE.Core.Models;

/// <summary>
/// Narrator-to-engine signal for quest state changes. Returned as part of FreeFormResponse
/// when the narrator detects quest-relevant events during conversation or free-form actions.
/// </summary>
public class QuestUpdates
{
    /// <summary>Quest ID the player has accepted (via NPC conversation).</summary>
    public string? AcceptedQuestId { get; set; }

    /// <summary>Quest ID the player has declined (via NPC conversation).</summary>
    public string? DeclinedQuestId { get; set; }

    /// <summary>Quest ID the player is turning in (via NPC conversation with quest giver/turn-in NPC).</summary>
    public string? TurnInQuestId { get; set; }

    /// <summary>
    /// Custom objective IDs that the narrator considers met based on the current interaction.
    /// Only evaluated on structured response paths (conversation, free-form).
    /// </summary>
    public List<string> CompletedCustomObjectives { get; set; } = [];

    /// <summary>
    /// AI-generated rich description for a newly accepted quest.
    /// Replaces or supplements the authored QuestDefinition.Description.
    /// </summary>
    public string? QuestDescription { get; set; }

    /// <summary>
    /// AI-generated description for the current stage when advancing.
    /// Stored in QuestProgress.StageNarrations for the journal.
    /// </summary>
    public string? StageDescription { get; set; }

    /// <summary>
    /// Optional quest failure recommendations from the narrator. The engine still validates
    /// whether the recommendation should actually mark the quest as failed.
    /// </summary>
    public Dictionary<string, string> FailureRecommended { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
