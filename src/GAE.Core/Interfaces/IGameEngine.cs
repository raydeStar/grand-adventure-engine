using GAE.Core.Models;

namespace GAE.Core.Interfaces;

public interface IGameEngine
{
    Task<ActionResult> ProcessActionAsync(string playerId, GameAction action, CancellationToken ct = default);
    Task<PlayerCharacter> CreateCharacterFromConceptAsync(CharacterConcept concept, CancellationToken ct = default);
    GameAction ParseCommand(string playerId, string rawInput);
    Task<CombatState?> GetActiveCombatAsync(string roomId, string worldId, CancellationToken ct = default);
}
