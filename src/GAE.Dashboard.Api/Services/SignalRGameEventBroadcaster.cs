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
        // Send to targeted groups only — never broadcast to All to avoid duplicates
        if (!string.IsNullOrEmpty(gameEvent.PlayerId))
            await _hubContext.Clients.Group($"player-{gameEvent.PlayerId}").SendAsync("PlayerEvent", gameEvent, ct);

        if (!string.IsNullOrEmpty(gameEvent.RoomId))
            await _hubContext.Clients.Group($"room-{gameEvent.RoomId}").SendAsync("RoomEvent", gameEvent, ct);

        // Always send to the admin feed so the event log panel stays current
        await _hubContext.Clients.Group("admins").SendAsync("AdminEvent", gameEvent, ct);
    }

    public async Task BroadcastActionResultAsync(ActionResult result, string playerId, CancellationToken ct = default)
    {
        // Send only to the specific player group — not All
        await _hubContext.Clients.Group($"player-{playerId}").SendAsync("ActionResult", result, ct);
    }
}
