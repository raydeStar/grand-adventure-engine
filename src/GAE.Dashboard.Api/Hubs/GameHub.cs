using GAE.Core.Models;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GAE.Dashboard.Api.Hubs;

[Authorize(Policy = DashboardPolicies.UserAccess)]
public class GameHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", $"Connected to Grand Adventure Engine at {DateTimeOffset.UtcNow:u}");
        await base.OnConnectedAsync();
    }

    public async Task JoinPlayerFeed(string playerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"player-{playerId}");
    }

    public async Task JoinRoomFeed(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"room-{roomId}");
    }

    public async Task LeaveRoomFeed(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"room-{roomId}");
    }

    /// <summary>Subscribes the caller to the admin event feed (receives all game events).</summary>
    public async Task JoinAdminFeed()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
    }
}
