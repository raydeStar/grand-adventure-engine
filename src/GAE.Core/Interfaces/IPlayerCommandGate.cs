using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Persists and checks cross-transport player holds so web and Discord obey the same human DM
/// decision, including after a service restart.
/// </summary>
public interface IPlayerCommandGate
{
    /// <summary>Returns the active hold for one player, or null when normal turns may proceed.</summary>
    Task<PlayerCommandHold?> GetHoldAsync(string playerId, CancellationToken ct = default);

    /// <summary>Places or refreshes one durable hold and returns its authoritative representation.</summary>
    Task<PlayerCommandHold> HoldAsync(
        string playerId,
        string reason,
        string heldBy,
        string? sourceActionId = null,
        CancellationToken ct = default);

    /// <summary>Removes an active hold. Returns false when the player was already free to act.</summary>
    Task<bool> ResumeAsync(string playerId, CancellationToken ct = default);
}
