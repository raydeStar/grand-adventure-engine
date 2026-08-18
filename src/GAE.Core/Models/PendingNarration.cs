namespace GAE.Core.Models;

/// <summary>
/// A narration owed to a player: the mechanical result of their action has already been delivered,
/// and the prose is still being written.
///
/// This exists because the narrator can be slow by design. A local Codex turn at maximum reasoning
/// effort takes tens of seconds, and holding the whole action behind it means the player stares at
/// nothing for half a minute. Mechanics are deterministic and resolve instantly, so they are sent
/// straight away and the prose is backfilled when it arrives.
///
/// Everything the narrator needs is captured at enqueue time. By the time turn two is narrated the
/// player may be on turn five, so narrating from current state would describe the wrong scene.
/// </summary>
public class PendingNarration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The action whose prose is outstanding, so the delivered entry can be updated in place.</summary>
    public string ActionId { get; set; } = string.Empty;

    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = WorldDefaults.DefaultWorldId;
    public string? RoomId { get; set; }

    /// <summary>
    /// Orders work within one player. Narration for a later turn must never be delivered before an
    /// earlier one, or the story log reads out of order.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>Which narrator operation to run, so the worker can rebuild the right call.</summary>
    public string Operation { get; set; } = "action";

    /// <summary>
    /// The narrator inputs, serialised at enqueue time. Held as JSON so the queue does not pin live
    /// object graphs and survives a restart.
    /// </summary>
    public string ContextJson { get; set; } = string.Empty;

    /// <summary>What the player was shown while waiting.</summary>
    public string PlaceholderNarration { get; set; } = string.Empty;

    /// <summary>The finished prose, once the narrator returns it.</summary>
    public string? Narration { get; set; }

    public PendingNarrationStatus Status { get; set; } = PendingNarrationStatus.Pending;

    public int AttemptCount { get; set; }

    /// <summary>Earliest time this may be attempted again; drives backoff.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Attempts before the debt is written off and the placeholder stands as final.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Marks a failed attempt and schedules the retry with exponential backoff, giving up once the
    /// attempt budget is spent so a permanently broken narrator cannot accumulate work forever.
    /// </summary>
    public void RecordFailure(string error)
    {
        AttemptCount++;
        LastError = string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim();

        if (AttemptCount >= MaxAttempts)
        {
            Status = PendingNarrationStatus.Abandoned;
            CompletedAt = DateTimeOffset.UtcNow;
            return;
        }

        Status = PendingNarrationStatus.Pending;
        // 15s, 30s, 60s, 120s — long enough to outlast a model restart without hammering it.
        var delaySeconds = 15 * Math.Pow(2, Math.Max(0, AttemptCount - 1));
        NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(delaySeconds, 300));
    }

    /// <summary>Marks the narration delivered.</summary>
    public void Complete(string narration)
    {
        Narration = narration;
        Status = PendingNarrationStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        LastError = null;
    }
}

public enum PendingNarrationStatus
{
    /// <summary>Waiting to be attempted.</summary>
    Pending,

    /// <summary>Claimed by a worker and currently being narrated.</summary>
    InFlight,

    /// <summary>Narration arrived and was delivered.</summary>
    Completed,

    /// <summary>The attempt budget was spent; the placeholder stands as the final text.</summary>
    Abandoned
}
