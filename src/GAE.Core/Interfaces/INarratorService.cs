using GAE.Core.Models;
using GAE.Core.Registry;

namespace GAE.Core.Interfaces;

/// <summary>
/// Request for AI-mediated stat translation between worlds with different stat systems.
/// </summary>
public class StatTranslationRequest
{
    public required string CharacterName { get; set; }
    public string? Class { get; set; }
    public string? Race { get; set; }
    public int Level { get; set; }
    public required string SourceWorldName { get; set; }
    public required string DestinationWorldName { get; set; }
    public required Dictionary<string, StatTranslationStat> SourceStats { get; set; }
    public required Dictionary<string, StatTranslationStat> DestinationStatDefs { get; set; }
    public string? PreviousTranslation { get; set; }
}

/// <summary>
/// A single stat entry with value, display name, and semantic tags for AI translation context.
/// </summary>
public class StatTranslationStat
{
    public string Display { get; set; } = string.Empty;
    public int Value { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public List<string> SemanticTags { get; set; } = [];
}

/// <summary>
/// AI response from stat translation: translated values, reasoning, and transition narrative.
/// </summary>
public class StatTranslationResponse
{
    public Dictionary<string, int> TranslatedStats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string TranslationNotes { get; set; } = string.Empty;
    public string Narrative { get; set; } = string.Empty;
}

public interface INarratorService
{
    Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default);
    Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default);
    Task<Npc> GenerateNpcAsync(Room room, string? faction = null, CancellationToken ct = default);
    Task<string> GenerateAsciiArtAsync(string subject, CancellationToken ct = default);
    Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default);
    Task<string?> ParseIntentAsync(string rawInput, CancellationToken ct = default);
    Task<FreeFormResponse> ProcessFreeFormAsync(PlayerCharacter player, Room room, string rawInput, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default);
    Task<FreeFormResponse> ProcessConversationTurnAsync(PlayerCharacter player, Room room, Npc npc, InteractionState interaction, string rawInput, CancellationToken ct = default);
    Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default);

    /// <summary>
    /// Generates a room for a Blind Adventure session using storyline context and visited room history.
    /// Returns a fully wired Room (with reverse exit) ready to persist.
    /// </summary>
    Task<Room> GenerateBlindAdventureRoomAsync(
        string roomId, string direction, Room sourceRoom,
        StorylineContext storyline, IReadOnlyList<string> visitedRoomSummaries,
        string? nextPlotBeat, int roomsRemaining, CancellationToken ct = default);

    /// <summary>
    /// Generates a dungeon entrance room scaled to the player's level.
    /// The room will have environment tags like "dungeon", "generated_dungeon", "difficulty_X"
    /// so that subsequent GenerateRoomAsync calls maintain dungeon theming.
    /// </summary>
    Task<Room> GenerateDungeonEntranceAsync(string dungeonId, int playerLevel, Room sourceRoom, CancellationToken ct = default);

    /// <summary>Vet a player-invented spell for the spellbook system (Option A: AI creates, engine scales).</summary>
    Task<SpellVetResponse?> VetSpellAsync(PlayerCharacter player, string spellDescription, Room room, CancellationToken ct = default);

    /// <summary>Evaluate an improvised (unregistered) spell attempt using the power-budget system.</summary>
    Task<ImprovisedSpellResult> EvaluateImprovisedSpellAsync(
        PlayerCharacter player, Room room, string spellName, string? target,
        int powerCap, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default);

    /// <summary>AI content generator: describe what you want and the AI fills in the structured details.</summary>
    Task<string> GenerateContentAsync(string contentType, string description, string? existingJson, CancellationToken ct = default);

    /// <summary>AI-driven character creation: takes a natural-language description and returns a JSON character concept.</summary>
    Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(string playerDescription, string? previousSheet, CancellationToken ct = default);
    /// <summary>Returns the currently active model identifier.</summary>
    string GetActiveModel();

    /// <summary>Sets the active model at runtime. Pass "default" to re-resolve on next call.</summary>
    void SetActiveModel(string model);

    /// <summary>Lists available models from the LM Studio /v1/models endpoint.</summary>
    Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// AI-mediated stat translation when a character crosses between worlds with different stat systems.
    /// Returns translated stats, reasoning notes, and a short narrative describing the transformation.
    /// Returns null when the AI is unavailable — caller should fall back to deterministic translation.
    /// </summary>
    Task<StatTranslationResponse?> TranslateStatsAsync(StatTranslationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Generates 2-3 sentences of transition narration for a realm crossing.
    /// Used when the stat translation was deterministic (no AI narrative was produced).
    /// Returns null when the AI is unavailable.
    /// </summary>
    Task<string?> NarrateRealmTransitionAsync(string playerName, string sourceWorldName, string destinationWorldName, string? portalHint, CancellationToken ct = default);

    /// <summary>
    /// Provides in-character narrator guidance about quests, exploration, and the main story.
    /// Acts as a helpful narrator voice giving atmospheric hints without spoiling puzzles.
    /// </summary>
    Task<string> ProvideGuidanceAsync(PlayerCharacter player, Room room, string? question, CancellationToken ct = default);

    /// <summary>
    /// Generates the hero's grand entrance intro after character creation.
    /// The narrator introduces himself, sets the story, and the world reacts to the hero's arrival.
    /// </summary>
    Task<string> GenerateHeroIntroAsync(PlayerCharacter player, Room room, CancellationToken ct = default);
}
