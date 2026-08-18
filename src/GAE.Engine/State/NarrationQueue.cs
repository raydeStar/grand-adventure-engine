using GAE.Core.Models;
using GAE.Engine.Data;
using GAE.Engine.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>Queue of narration owed to players whose mechanical results have already been delivered.</summary>
public interface INarrationQueue
{
    /// <summary>Records that a narration is owed, returning the stored item.</summary>
    Task<PendingNarration> EnqueueAsync(PendingNarration pending, CancellationToken ct = default);

    /// <summary>
    /// Claims the next narration that is due, respecting per-player ordering, or null when there is
    /// nothing to do. Claiming marks it in-flight so concurrent workers cannot take the same item.
    /// </summary>
    Task<PendingNarration?> ClaimNextAsync(CancellationToken ct = default);

    /// <summary>Stores finished prose.</summary>
    Task CompleteAsync(PendingNarration pending, string narration, CancellationToken ct = default);

    /// <summary>Records a failed attempt and schedules a retry, or abandons it once the budget is spent.</summary>
    Task FailAsync(PendingNarration pending, string error, CancellationToken ct = default);

    /// <summary>Outstanding items for a player, oldest first. Used to show what is still being written.</summary>
    Task<IReadOnlyList<PendingNarration>> GetOutstandingForPlayerAsync(string playerId, CancellationToken ct = default);

    /// <summary>Next sequence number for a player, so ordering survives restarts.</summary>
    Task<long> NextSequenceAsync(string playerId, CancellationToken ct = default);

    /// <summary>
    /// Returns anything left in-flight to the pending state. Called at startup: an item claimed by a
    /// process that then died would otherwise stay in-flight forever.
    /// </summary>
    Task<int> RecoverStaleInFlightAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>PostgreSQL-backed narration queue.</summary>
public class EfCoreNarrationQueue : INarrationQueue
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<EfCoreNarrationQueue> _logger;

    // Claiming is serialised in-process so two workers cannot take the same item. A single container
    // owns the queue; scaling out would need SELECT ... FOR UPDATE SKIP LOCKED instead.
    private static readonly SemaphoreSlim ClaimLock = new(1, 1);

    public EfCoreNarrationQueue(IDbContextFactory<GaeDbContext> dbFactory, ILogger<EfCoreNarrationQueue> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<PendingNarration> EnqueueAsync(PendingNarration pending, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.PendingNarrations.Add(ToEntity(pending));
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Narration deferred for player {PlayerId} action {ActionId} (sequence {Sequence}); prose will follow",
            pending.PlayerId, pending.ActionId, pending.Sequence);

        return pending;
    }

    public async Task<PendingNarration?> ClaimNextAsync(CancellationToken ct = default)
    {
        await ClaimLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTimeOffset.UtcNow;

            // Per-player FIFO: only the lowest outstanding sequence for a player is eligible, so a
            // later turn's prose can never be delivered before an earlier one.
            var candidates = await db.PendingNarrations
                .Where(n => n.Status == PendingNarrationStatus.Pending && n.NextAttemptAt <= now)
                .OrderBy(n => n.CreatedAt)
                .Take(50)
                .ToListAsync(ct);

            if (candidates.Count == 0)
                return null;

            var blockedPlayers = await db.PendingNarrations
                .Where(n => n.Status == PendingNarrationStatus.InFlight)
                .Select(n => n.PlayerId)
                .Distinct()
                .ToListAsync(ct);

            var claimable = candidates
                .Where(n => !blockedPlayers.Contains(n.PlayerId))
                .GroupBy(n => n.PlayerId)
                .Select(group => group.OrderBy(n => n.Sequence).First())
                .OrderBy(n => n.CreatedAt)
                .FirstOrDefault();

            if (claimable is null)
                return null;

            claimable.Status = PendingNarrationStatus.InFlight;
            await db.SaveChangesAsync(ct);
            return ToModel(claimable);
        }
        finally
        {
            ClaimLock.Release();
        }
    }

    public async Task CompleteAsync(PendingNarration pending, string narration, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PendingNarrations.FirstOrDefaultAsync(n => n.Id == pending.Id, ct);
        if (entity is null) return;

        pending.Complete(narration);
        entity.Narration = pending.Narration;
        entity.Status = pending.Status;
        entity.CompletedAt = pending.CompletedAt;
        entity.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(PendingNarration pending, string error, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PendingNarrations.FirstOrDefaultAsync(n => n.Id == pending.Id, ct);
        if (entity is null) return;

        pending.AttemptCount = entity.AttemptCount;
        pending.RecordFailure(error);

        entity.AttemptCount = pending.AttemptCount;
        entity.Status = pending.Status;
        entity.NextAttemptAt = pending.NextAttemptAt;
        entity.CompletedAt = pending.CompletedAt;
        entity.LastError = pending.LastError;
        await db.SaveChangesAsync(ct);

        if (pending.Status == PendingNarrationStatus.Abandoned)
        {
            _logger.LogWarning(
                "Giving up on narration for action {ActionId} after {Attempts} attempts; the placeholder stands. Last error: {Error}",
                pending.ActionId, pending.AttemptCount, pending.LastError);
        }
    }

    public async Task<IReadOnlyList<PendingNarration>> GetOutstandingForPlayerAsync(string playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PendingNarrations
            .Where(n => n.PlayerId == playerId
                        && (n.Status == PendingNarrationStatus.Pending || n.Status == PendingNarrationStatus.InFlight))
            .OrderBy(n => n.Sequence)
            .ToListAsync(ct);

        return rows.Select(ToModel).ToList();
    }

    public async Task<long> NextSequenceAsync(string playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var highest = await db.PendingNarrations
            .Where(n => n.PlayerId == playerId)
            .Select(n => (long?)n.Sequence)
            .MaxAsync(ct);

        return (highest ?? 0) + 1;
    }

    public async Task<int> RecoverStaleInFlightAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - olderThan;

        var stale = await db.PendingNarrations
            .Where(n => n.Status == PendingNarrationStatus.InFlight && n.CreatedAt <= cutoff)
            .ToListAsync(ct);

        foreach (var entity in stale)
        {
            entity.Status = PendingNarrationStatus.Pending;
            entity.NextAttemptAt = DateTimeOffset.UtcNow;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Returned {Count} stranded narration(s) to the queue", stale.Count);
        }

        return stale.Count;
    }

    private static PendingNarrationEntity ToEntity(PendingNarration model) => new()
    {
        Id = model.Id,
        ActionId = model.ActionId,
        PlayerId = model.PlayerId,
        WorldId = model.WorldId,
        RoomId = model.RoomId,
        Sequence = model.Sequence,
        Operation = model.Operation,
        ContextJson = model.ContextJson,
        PlaceholderNarration = model.PlaceholderNarration,
        Narration = model.Narration,
        Status = model.Status,
        AttemptCount = model.AttemptCount,
        NextAttemptAt = model.NextAttemptAt,
        CreatedAt = model.CreatedAt,
        CompletedAt = model.CompletedAt,
        LastError = model.LastError
    };

    private static PendingNarration ToModel(PendingNarrationEntity entity) => new()
    {
        Id = entity.Id,
        ActionId = entity.ActionId,
        PlayerId = entity.PlayerId,
        WorldId = entity.WorldId,
        RoomId = entity.RoomId,
        Sequence = entity.Sequence,
        Operation = entity.Operation,
        ContextJson = entity.ContextJson,
        PlaceholderNarration = entity.PlaceholderNarration,
        Narration = entity.Narration,
        Status = entity.Status,
        AttemptCount = entity.AttemptCount,
        NextAttemptAt = entity.NextAttemptAt,
        CreatedAt = entity.CreatedAt,
        CompletedAt = entity.CompletedAt,
        LastError = entity.LastError
    };
}
