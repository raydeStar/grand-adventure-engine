using GAE.Engine.Worlds;
using Microsoft.EntityFrameworkCore;

namespace GAE.Engine.Data;

/// <summary>
/// PostgreSQL-backed implementation of the world repository used during the phased
/// multi-world rollout.
/// </summary>
public class EfCoreWorldRepository : IWorldRepository
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;

    public EfCoreWorldRepository(IDbContextFactory<GaeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<World?> GetWorldAsync(string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Worlds.AsNoTracking().FirstOrDefaultAsync(w => w.Id == worldId, ct);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<World>> GetAllWorldsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Worlds.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
        return entities.Select(e => e.ToDomain()).ToList();
    }

    /// <inheritdoc />
    public async Task SaveWorldAsync(World world, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Worlds.FindAsync([world.Id], ct);
        if (existing is null)
            db.Worlds.Add(WorldEntity.FromDomain(world));
        else
            existing.UpdateFrom(world);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveWorldAsync(string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Worlds.Where(w => w.Id == worldId).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    /// <inheritdoc />
    public async Task<PlayerWorldState?> GetPlayerWorldStateAsync(string playerId, string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PlayerWorldStates.AsNoTracking().FirstOrDefaultAsync(s => s.PlayerId == playerId && s.WorldId == worldId, ct);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task SavePlayerWorldStateAsync(PlayerWorldState state, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.PlayerWorldStates.FindAsync([state.PlayerId, state.WorldId], ct);
        if (existing is null)
            db.PlayerWorldStates.Add(PlayerWorldStateEntity.FromDomain(state));
        else
            existing.UpdateFrom(state);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WorldStatSnapshot?> GetStatSnapshotAsync(string playerId, string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorldStatSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.PlayerId == playerId && s.WorldId == worldId, ct);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task SaveStatSnapshotAsync(WorldStatSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorldStatSnapshots.FindAsync([snapshot.Id], ct);
        if (existing is null)
            db.WorldStatSnapshots.Add(WorldStatSnapshotEntity.FromDomain(snapshot));
        else
            existing.UpdateFrom(snapshot);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<StatTranslationHistory?> GetTranslationHistoryAsync(string playerId, string sourceWorldId, string destinationWorldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.StatTranslationHistory.AsNoTracking()
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync(h => h.PlayerId == playerId && h.SourceWorldId == sourceWorldId && h.DestinationWorldId == destinationWorldId, ct);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task SaveTranslationHistoryAsync(StatTranslationHistory history, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.StatTranslationHistory.FindAsync([history.Id], ct);
        if (existing is null)
            db.StatTranslationHistory.Add(StatTranslationHistoryEntity.FromDomain(history));
        else
            existing.UpdateFrom(history);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WorldNpcState?> GetWorldNpcStateAsync(string npcId, string worldId, string? playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorldNpcStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.NpcId == npcId && s.WorldId == worldId && s.PlayerId == playerId, ct);
        return entity?.ToDomain();
    }

    /// <inheritdoc />
    public async Task SaveWorldNpcStateAsync(WorldNpcState state, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.WorldNpcStates.FindAsync([state.Id], ct);
        if (existing is null)
            db.WorldNpcStates.Add(WorldNpcStateEntity.FromDomain(state));
        else
            existing.UpdateFrom(state);
        await db.SaveChangesAsync(ct);
    }
}