using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Dashboard.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GAE.Dashboard.Api.Services;

public class SignalRGameEventBroadcaster : IGameEventBroadcaster
{
    private readonly IHubContext<GameHub> _hubContext;

    public SignalRGameEventBroadcaster(IHubContext<GameHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastEventAsync(GameEvent gameEvent, CancellationToken ct = default)
    {
        // Broadcast to all connected clients
        await _hubContext.Clients.All.SendAsync("GameEvent", gameEvent, ct);

        // Also send to specific player and room groups
        if (!string.IsNullOrEmpty(gameEvent.PlayerId))
            await _hubContext.Clients.Group($"player-{gameEvent.PlayerId}").SendAsync("PlayerEvent", gameEvent, ct);

        if (!string.IsNullOrEmpty(gameEvent.RoomId))
            await _hubContext.Clients.Group($"room-{gameEvent.RoomId}").SendAsync("RoomEvent", gameEvent, ct);
    }

    public async Task BroadcastActionResultAsync(ActionResult result, string playerId, CancellationToken ct = default)
    {
        await _hubContext.Clients.Group($"player-{playerId}").SendAsync("ActionResult", result, ct);
        await _hubContext.Clients.All.SendAsync("ActionResult", result, ct);
    }
}
