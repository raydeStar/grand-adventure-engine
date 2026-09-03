using GAE.Core.Models;

namespace GAE.Core.Interfaces;

public interface IGameEngine
{
    Task<ActionResult> ProcessActionAsync(string playerId, GameAction action, CancellationToken ct = default);

    /// <summary>
    /// Invokes one registered spell as a DM-authored world effect without consuming a player's
    /// mana, teaching the spell, or granting automatic kill rewards.
    /// </summary>
    Task<ActionResult> InvokeDmSpellAsync(
        string playerId,
        string spellId,
        string targetEntityId,
        string actionId,
        CancellationToken ct = default);
    Task<PlayerCharacter> CreateCharacterFromConceptAsync(CharacterConcept concept, CancellationToken ct = default);
    GameAction ParseCommand(string playerId, string rawInput);
    Task<CombatState?> GetActiveCombatAsync(string roomId, string worldId, CancellationToken ct = default);
    string? CheckAndApplyLevelUp(PlayerCharacter player);

    /// <summary>
    /// Generates the hero intro narration for a newly created character, saves it as the first
    /// story entry, and returns the narration text. Call this immediately after character creation.
    /// </summary>
    Task<string> GenerateHeroIntroAsync(string playerId, CancellationToken ct = default);
}
