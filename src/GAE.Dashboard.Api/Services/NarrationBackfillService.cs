using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.State;

namespace GAE.Dashboard.Api.Services;

/// <summary>
/// Drains the deferred-narration queue and delivers prose to players after the fact.
///
/// A narrator can be slow by design — a local Codex turn at maximum reasoning effort takes tens of
/// seconds — so the engine sends the mechanical result immediately with a placeholder and records
/// that prose is owed. This service writes that prose and pushes it out, which is what turns a
/// thirty-second stall into a thirty-second wait the player can play through.
/// </summary>
public class NarrationBackfillService : BackgroundService
{
    private readonly INarrationQueue _queue;
    private readonly INarratorService _narrator;
    private readonly IStateManager _stateManager;
    private readonly IGameEventBroadcaster _broadcaster;
    private readonly IDiscordNotifier? _discord;
    private readonly ILogger<NarrationBackfillService> _logger;

    /// <summary>How long to wait when the queue is empty. Short enough to feel prompt, idle enough to be free.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    /// <summary>An item claimed but never finished is presumed stranded after this long.</summary>
    private static readonly TimeSpan StaleInFlightAfter = TimeSpan.FromMinutes(15);

    public NarrationBackfillService(
        INarrationQueue queue,
        INarratorService narrator,
        IStateManager stateManager,
        IGameEventBroadcaster broadcaster,
        ILogger<NarrationBackfillService> logger,
        IDiscordNotifier? discord = null)
    {
        _queue = queue;
        _narrator = narrator;
        _stateManager = stateManager;
        _broadcaster = broadcaster;
        _logger = logger;
        _discord = discord;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Anything left in-flight belongs to a process that died mid-narration; put it back.
        try
        {
            await _queue.RecoverStaleInFlightAsync(TimeSpan.Zero, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not recover stranded narrations at startup");
        }

        _logger.LogInformation("Narration backfill service started; deferred prose will be delivered as it arrives");

        var lastStaleSweep = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastStaleSweep > StaleInFlightAfter)
                {
                    await _queue.RecoverStaleInFlightAsync(StaleInFlightAfter, stoppingToken);
                    lastStaleSweep = DateTimeOffset.UtcNow;
                }

                var pending = await _queue.ClaimNextAsync(stoppingToken);
                if (pending is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                await ProcessAsync(pending, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must survive anything; a crash here would silently stop all backfill.
                _logger.LogError(ex, "Narration backfill loop error");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        _logger.LogInformation("Narration backfill service stopped");
    }

    private async Task ProcessAsync(PendingNarration pending, CancellationToken ct)
    {
        try
        {
            var context = JsonSerializer.Deserialize<NarratorContext>(pending.ContextJson, NarrationContextJson.Options);
            if (context is null)
            {
                await _queue.FailAsync(pending, "stored narrator context could not be read", ct);
                return;
            }

            var narration = await _narrator.NarrateActionAsync(context, ct);
            if (string.IsNullOrWhiteSpace(narration))
            {
                await _queue.FailAsync(pending, "narrator returned nothing", ct);
                return;
            }

            await _queue.CompleteAsync(pending, narration, ct);
            await DeliverAsync(pending, narration, ct);

            _logger.LogInformation(
                "Delivered deferred narration for action {ActionId} after {Elapsed:0.0}s",
                pending.ActionId, (DateTimeOffset.UtcNow - pending.CreatedAt).TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave it in-flight so startup recovery picks it back up.
            throw;
        }
        catch (Exception ex)
        {
            await _queue.FailAsync(pending, ex.Message, CancellationToken.None);
        }
    }

    /// <summary>
    /// Pushes finished prose to wherever the player is. The story entry is updated first so a
    /// reload shows the real text, then the live channels are nudged.
    /// </summary>
    private async Task DeliverAsync(PendingNarration pending, string narration, CancellationToken ct)
    {
        try
        {
            await _stateManager.UpdateStoryNarrationAsync(pending.ActionId, narration, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the stored story entry for action {ActionId}", pending.ActionId);
        }

        try
        {
            await _broadcaster.BroadcastEventAsync(new GameEvent
            {
                Type = GameEventType.StoryAdvanced,
                ActionId = pending.ActionId,
                PlayerId = pending.PlayerId,
                RoomId = pending.RoomId,
                Summary = "Narration completed.",
                Narration = narration,
                Data = new Dictionary<string, object?>
                {
                    // Lets the client replace the placeholder in place rather than appending a new entry.
                    ["actionId"] = pending.ActionId,
                    ["deferredNarration"] = true
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast deferred narration for action {ActionId}", pending.ActionId);
        }

        if (_discord is not null)
        {
            try
            {
                await _discord.PostToPlayerThreadAsync(pending.PlayerId, $"*{narration}*", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not post deferred narration to Discord for player {PlayerId}", pending.PlayerId);
            }
        }
    }
}

/// <summary>
/// Serialisation settings for the stored narrator context. Kept in one place so the enqueue and
/// dequeue sides cannot drift apart.
/// </summary>
public static class NarrationContextJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
