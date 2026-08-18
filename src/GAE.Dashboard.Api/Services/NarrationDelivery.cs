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
    private readonly IDiscordNotifier? _discord;
    private readonly ILogger<NarrationDelivery> _logger;

    public NarrationDelivery(
        IStateManager stateManager,
        IGameEventBroadcaster broadcaster,
        ILogger<NarrationDelivery> logger,
        IDiscordNotifier? discord = null)
    {
        _stateManager = stateManager;
        _broadcaster = broadcaster;
        _logger = logger;
        _discord = discord;
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

        if (_discord is not null)
        {
            try
            {
                await _discord.PostToPlayerThreadAsync(playerId, $"*{narration}*", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not post deferred narration to Discord for player {PlayerId}", playerId);
            }
        }
    }
}
