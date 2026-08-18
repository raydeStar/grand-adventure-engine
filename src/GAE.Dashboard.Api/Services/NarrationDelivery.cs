using GAE.Core.Interfaces;
using GAE.Core.Models;

namespace GAE.Dashboard.Api.Services;

/// <summary>Sends finished prose to wherever the player is watching.</summary>
public interface INarrationDelivery
{
    /// <summary>
    /// Updates the recorded story entry and pushes the prose to live channels. Every step is
    /// best-effort and independent: a Discord outage must not prevent the dashboard from updating.
    /// </summary>
    Task DeliverAsync(string actionId, string playerId, string? roomId, string narration, CancellationToken ct = default);
}

public class NarrationDelivery : INarrationDelivery
{
    private readonly IStateManager _stateManager;
    private readonly IGameEventBroadcaster _broadcaster;
    private readonly ILogger<NarrationDelivery> _logger;

    // Resolved on use rather than injected. The Discord bot depends on the narrator, and the narrator
    // now depends on this service, so taking IDiscordNotifier as a constructor argument closes a cycle:
    // narrator -> delivery -> Discord bot -> narrator. The container resolves those lazy factories by
    // recursing rather than reporting the cycle, which built a fresh narrator on every hop.
    private readonly IServiceProvider _services;

    public NarrationDelivery(
        IStateManager stateManager,
        IGameEventBroadcaster broadcaster,
        ILogger<NarrationDelivery> logger,
        IServiceProvider services)
    {
        _stateManager = stateManager;
        _broadcaster = broadcaster;
        _logger = logger;
        _services = services;
    }

    public async Task DeliverAsync(string actionId, string playerId, string? roomId, string narration, CancellationToken ct = default)
    {
        // Persist first: a reload should show the real prose even if every live channel fails.
        try
        {
            await _stateManager.UpdateStoryNarrationAsync(actionId, narration, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the stored story entry for action {ActionId}", actionId);
        }

        try
        {
            await _broadcaster.BroadcastEventAsync(new GameEvent
            {
                Type = GameEventType.StoryAdvanced,
                ActionId = actionId,
                PlayerId = playerId,
                RoomId = roomId,
                Summary = "Narration completed.",
                Narration = narration,
                Data = new Dictionary<string, object?>
                {
                    // The client keys on these to replace its placeholder in place rather than
                    // appending the prose as a second entry.
                    ["actionId"] = actionId,
                    ["deferredNarration"] = true,
                    ["narration"] = narration
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast deferred narration for action {ActionId}", actionId);
        }

        var discord = _services.GetService<IDiscordNotifier>();
        if (discord is not null)
        {
            try
            {
                await discord.PostToPlayerThreadAsync(playerId, $"*{narration}*", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not post deferred narration to Discord for player {PlayerId}", playerId);
            }
        }
    }
}
