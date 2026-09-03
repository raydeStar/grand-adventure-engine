using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Data;

/// <summary>
/// PostgreSQL-backed command gate. A dedicated row makes a hold immediately visible to new and
/// already-queued turns without racing a long-running player save.
/// </summary>
public class EfCorePlayerCommandGate : IPlayerCommandGate
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<EfCorePlayerCommandGate> _logger;

    public EfCorePlayerCommandGate(
        IDbContextFactory<GaeDbContext> dbFactory,
        ILogger<EfCorePlayerCommandGate> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PlayerCommandHold?> GetHoldAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hold = await db.PlayerCommandHolds.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.PlayerId == playerId, ct);
        return hold?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<PlayerCommandHold> HoldAsync(
        string playerId,
        string reason,
        string heldBy,
        string? sourceActionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            throw new ArgumentException("A player ID is required.", nameof(playerId));

        var now = DateTimeOffset.UtcNow;
        var boundedReason = string.IsNullOrWhiteSpace(reason)
            ? "The Dungeon Master is reviewing this scene."
            : reason.Trim()[..Math.Min(reason.Trim().Length, 300)];
        var boundedActor = string.IsNullOrWhiteSpace(heldBy)
            ? "admin"
            : heldBy.Trim()[..Math.Min(heldBy.Trim().Length, 120)];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hold = await db.PlayerCommandHolds.SingleOrDefaultAsync(candidate => candidate.PlayerId == playerId, ct);
        if (hold is null)
        {
            hold = new PlayerCommandHoldEntity { PlayerId = playerId };
            db.PlayerCommandHolds.Add(hold);
        }

        hold.Reason = boundedReason;
        hold.HeldBy = boundedActor;
        hold.HeldAt = now;
        hold.SourceActionId = string.IsNullOrWhiteSpace(sourceActionId)
            ? null
            : sourceActionId.Trim()[..Math.Min(sourceActionId.Trim().Length, 120)];
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("DM command hold placed on player {PlayerId} by {HeldBy}. The velvet rope is now quite literal.", playerId, boundedActor);
        return hold.ToDomain();
    }

    /// <inheritdoc />
    public async Task<bool> ResumeAsync(string playerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return false;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.PlayerCommandHolds
            .Where(candidate => candidate.PlayerId == playerId)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            _logger.LogInformation("DM command hold released for player {PlayerId}. The drawbridge descends.", playerId);
        return deleted > 0;
    }
}
