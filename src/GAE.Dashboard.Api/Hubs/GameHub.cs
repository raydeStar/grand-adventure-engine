using System.Security.Claims;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Dashboard.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GAE.Dashboard.Api.Hubs;

[Authorize(Policy = DashboardPolicies.UserAccess)]
public class GameHub : Hub
{
    private readonly IStateManager _stateManager;

    public GameHub(IStateManager stateManager)
    {
        _stateManager = stateManager;
    }

    /// <summary>Confirms the authenticated real-time connection is ready for subscriptions.</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", $"Connected to Grand Adventure Engine at {DateTimeOffset.UtcNow:u}");
        await base.OnConnectedAsync();
    }

    /// <summary>Subscribes an authenticated player session to updates for a character it owns (admins may watch anyone).</summary>
    public async Task JoinPlayerFeed(string playerId)
    {
        var player = string.IsNullOrWhiteSpace(playerId) ? null : await _stateManager.GetPlayerAsync(playerId.Trim());
        if (player is null || !CanAccess(player))
            throw new HubException("That character is not yours to watch.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"player-{player.Id}");
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

    private bool CanAccess(PlayerCharacter player)
    {
        var user = Context.User;
        if (user?.IsInRole(DashboardRoles.Admin) ?? false)
            return true;

        var username = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.Identity?.Name;
        return !string.IsNullOrEmpty(player.OwnerId)
            && string.Equals(player.OwnerId, username, StringComparison.OrdinalIgnoreCase);
    }
}
