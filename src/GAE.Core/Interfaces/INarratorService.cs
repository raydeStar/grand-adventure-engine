using GAE.Core.Models;

namespace GAE.Core.Interfaces;

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


    /// <summary>AI-driven character creation: takes a natural-language description and returns a JSON character concept.</summary>
    Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(string playerDescription, string? previousSheet, CancellationToken ct = default);
    /// <summary>Returns the currently active model identifier.</summary>
    string GetActiveModel();

    /// <summary>Sets the active model at runtime. Pass "default" to re-resolve on next call.</summary>
    void SetActiveModel(string model);

    /// <summary>Lists available models from the LM Studio /v1/models endpoint.</summary>
    Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default);
}
