using GAE.Core.Interfaces;
using GAE.Core.Models;

namespace GAE.Dashboard.Api.Security;

/// <summary>
/// Resolves the scoped world context for each request.
/// Priority: explicit worldId query/header, then player-derived world.
/// </summary>
public class WorldContextMiddleware
{
    private readonly RequestDelegate _next;

    public WorldContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IWorldContext worldContext, IStateManager stateManager)
    {
        var worldId = ExtractWorldId(context);

        if (string.IsNullOrWhiteSpace(worldId))
        {
            var playerId = ExtractPlayerId(context);
            if (!string.IsNullOrWhiteSpace(playerId))
            {
                var player = await stateManager.GetPlayerAsync(playerId, context.RequestAborted);
                worldId = player?.ActiveWorldId;
            }
        }

        worldContext.SetCurrentWorld(string.IsNullOrWhiteSpace(worldId) ? WorldDefaults.DefaultWorldId : worldId);
        await _next(context);
    }

    private static string? ExtractWorldId(HttpContext context)
    {
        if (context.Request.Query.TryGetValue("worldId", out var worldIdFromQuery) && !string.IsNullOrWhiteSpace(worldIdFromQuery.ToString()))
            return worldIdFromQuery.ToString();

        if (context.Request.Headers.TryGetValue("X-World-Id", out var worldIdFromHeader) && !string.IsNullOrWhiteSpace(worldIdFromHeader.ToString()))
            return worldIdFromHeader.ToString();

        if (context.Items.TryGetValue("worldId", out var worldIdFromItems) && worldIdFromItems is string worldId && !string.IsNullOrWhiteSpace(worldId))
            return worldId;

        return null;
    }

    private static string? ExtractPlayerId(HttpContext context)
    {
        if (context.Request.RouteValues.TryGetValue("playerId", out var routePlayerId)
            && routePlayerId is string routeId
            && !string.IsNullOrWhiteSpace(routeId))
        {
            return routeId;
        }

        if (context.Request.Query.TryGetValue("playerId", out var playerIdFromQuery) && !string.IsNullOrWhiteSpace(playerIdFromQuery.ToString()))
            return playerIdFromQuery.ToString();

        if (context.Request.Headers.TryGetValue("X-Player-Id", out var playerIdFromHeader) && !string.IsNullOrWhiteSpace(playerIdFromHeader.ToString()))
            return playerIdFromHeader.ToString();

        return null;
    }
}
