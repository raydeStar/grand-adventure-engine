using GAE.Core.Models;

namespace GAE.Engine.Worlds;

/// <summary>
/// Handles cross-world player transfers including snapshotting and stat translation.
/// </summary>
public interface IRealmTravelService
{
    /// <summary>
    /// Transfers a player into another world.
    /// </summary>
    Task<ActionResult> TransferPlayerAsync(string playerId, string destinationWorldId, string initiator, CancellationToken ct = default);
}
