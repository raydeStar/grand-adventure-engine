namespace GAE.Narrator.Prompts;

/// <summary>
/// Centralized prompt templates for quest narration injection points.
/// Used by <see cref="NarratorService"/> when building quest-related narrator prompts.
/// </summary>
public static class QuestPrompts
{
    /// <summary>
    /// Narrator instructions for conversation-mode quest interactions (offer, accept, decline, turn-in, failure).
    /// Injected into the conversation system prompt when the NPC has quests or the player has active quests involving the NPC.
    /// </summary>
    public const string ConversationQuestInstructions = """
        QUEST INTERACTIONS:
        This NPC may offer quests, accept quest turn-ins, or discuss quest progress.
        - If the player ACCEPTS a quest during conversation, return "acceptedQuestId" with the quest ID.
        - If the player DECLINES a quest, return "declinedQuestId" with the quest ID.
        - If the player is TURNING IN a completed quest, return "turnInQuestId" with the quest ID.
        - If a custom objective is met during this conversation, list its ID in "completedCustomObjectives".
        - If the player clearly ruins, betrays, or invalidates a quest, you may set questUpdates.failureRecommended as { "quest_id": "reason" }.
        - If you provide a quest, write a rich "questDescription" (2-3 sentences, in character).
        - For stage transitions, write a "stageDescription" capturing the narrative beat.
        - NEVER invent quest IDs. Only use IDs from the QUEST CONTEXT provided.
        - If the NPC has no quests or the player doesn't ask, omit questUpdates entirely.
        """;

    /// <summary>
    /// Narrator instructions for free-form quest objective completion.
    /// Injected into the free-form action system prompt when the player has active quests.
    /// </summary>
    public const string FreeFormQuestInstructions =
        "If the player's action completes a custom quest objective, include it in questUpdates.completedCustomObjectives.";

    /// <summary>
    /// Narrator instruction appended to quest offer context — tells the AI to weave the offer naturally.
    /// </summary>
    public const string QuestOfferNarratorHint =
        "Work this quest offer naturally into the conversation. Do NOT say 'Quest Available' — the NPC has a problem and is asking for help.";

    /// <summary>
    /// Narrator instruction for acknowledging objective progress — avoid gamified language.
    /// </summary>
    public const string ObjectiveProgressNarratorHint =
        "Acknowledge this progress naturally. A 3/5 kill count might be 'The vermin are thinning...' not 'You have killed 3 of 5 rats.'";

    /// <summary>
    /// Narrator instruction for stage transitions — dramatic beats.
    /// </summary>
    public const string StageTransitionNarratorHint =
        "The situation has evolved. Narrate the transition dramatically.";

    /// <summary>
    /// Narrator instruction for quest completion — natural reward narration.
    /// </summary>
    public const string QuestCompletionNarratorHint =
        "Narrate the conclusion. Describe the NPC's reaction. Mention rewards earned naturally (gold pouch, XP gained as 'you feel wiser').";

    /// <summary>
    /// Narrator instruction for quest failure — consequences carry weight.
    /// </summary>
    public const string QuestFailureNarratorHint =
        "This quest has failed. Narrate the consequence with weight — actions have meaning here.";
}
