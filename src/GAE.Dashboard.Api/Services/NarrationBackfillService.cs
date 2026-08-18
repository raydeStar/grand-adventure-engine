using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.State;

namespace GAE.Dashboard.Api.Services;

/// <summary>
/// Drains the deferred-narration queue and delivers prose that this process did not finish itself.
///
/// In the normal case the request path keeps hold of its own slow narrator call and settles the queue
/// row when it lands, so this service has nothing to do. It exists for the cases that path cannot
/// cover: a process that died mid-narration, and a narrator that failed and needs retrying with
/// backoff.
/// </summary>
public class NarrationBackfillService : BackgroundService
{
    private readonly INarrationQueue _queue;
    private readonly INarratorService _narrator;
    private readonly INarrationDelivery _delivery;
    private readonly ILogger<NarrationBackfillService> _logger;

    /// <summary>How long to wait when the queue is empty. Prompt enough to feel responsive, idle enough to be free.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    /// <summary>An item claimed but never finished is presumed stranded after this long.</summary>
    private static readonly TimeSpan StaleInFlightAfter = TimeSpan.FromMinutes(15);

    public NarrationBackfillService(
        INarrationQueue queue,
        INarratorService narrator,
        INarrationDelivery delivery,
        ILogger<NarrationBackfillService> logger)
    {
        _queue = queue;
        _narrator = narrator;
        _delivery = delivery;
        _logger = logger;
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

        _logger.LogInformation("Narration backfill service started; unfinished prose will be retried and delivered");

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
            await _delivery.DeliverAsync(pending.ActionId, pending.PlayerId, pending.RoomId, narration, ct);

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
