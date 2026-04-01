using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Broadcasts game events to connected clients (SignalR, webhooks, etc.).
/// </summary>
public interface IGameEventBroadcaster
{
    Task BroadcastEventAsync(GameEvent gameEvent, CancellationToken ct = default);
    Task BroadcastActionResultAsync(ActionResult result, string playerId, CancellationToken ct = default);
}
