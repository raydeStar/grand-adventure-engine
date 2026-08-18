using GAE.Core.Models;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GAE.Dashboard.Api.Hubs;

[Authorize(Policy = DashboardPolicies.UserAccess)]
public class GameHub : Hub
{
    /// <summary>Confirms the authenticated real-time connection is ready for subscriptions.</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", $"Connected to Grand Adventure Engine at {DateTimeOffset.UtcNow:u}");
        await base.OnConnectedAsync();
    }

    /// <summary>Subscribes an authenticated player session to updates for a selected character.</summary>
    public async Task JoinPlayerFeed(string playerId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"player-{playerId}");
    }

    /// <summary>Subscribes an authenticated player session to activity in a selected room.</summary>
    public async Task JoinRoomFeed(string roomId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"room-{roomId}");
    }

    /// <summary>Stops room activity delivery when the player leaves or changes views.</summary>
    public async Task LeaveRoomFeed(string roomId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"room-{roomId}");
    }

    /// <summary>Subscribes the caller to the admin event feed (receives all game events).</summary>
    public async Task JoinAdminFeed()
    {
        if (!(Context.User?.IsInRole(DashboardRoles.Admin) ?? false))
            throw new HubException("The admin feed remains behind the velvet rope.");

        await Groups.AddToGroupAsync(Context.ConnectionId, "admins");
    }
}
