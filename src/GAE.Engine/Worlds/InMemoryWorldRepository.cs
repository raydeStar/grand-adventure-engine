using System.Collections.Concurrent;

namespace GAE.Engine.Worlds;

/// <summary>
/// Lightweight in-memory world repository used by Development file-mode runs and unit tests.
/// This keeps phase-one bootstrap logic available everywhere before full world context
/// routing is introduced in later phases.
/// </summary>
public class InMemoryWorldRepository : IWorldRepository
{
    private readonly ConcurrentDictionary<string, World> _worlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlayerWorldState> _playerWorldStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorldStatSnapshot> _statSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StatTranslationHistory> _translationHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorldNpcState> _npcStates = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<World?> GetWorldAsync(string worldId, CancellationToken ct = default)
        => Task.FromResult(_worlds.GetValueOrDefault(worldId));

    /// <inheritdoc />
    public Task<IReadOnlyList<World>> GetAllWorldsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<World>>(_worlds.Values.OrderBy(w => w.Name).ToList());

    /// <inheritdoc />
    public Task SaveWorldAsync(World world, CancellationToken ct = default)
    {
        _worlds[world.Id] = world;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveWorldAsync(string worldId, CancellationToken ct = default)
        => Task.FromResult(_worlds.TryRemove(worldId, out _));

    /// <inheritdoc />
    public Task<PlayerWorldState?> GetPlayerWorldStateAsync(string playerId, string worldId, CancellationToken ct = default)
        => Task.FromResult(_playerWorldStates.GetValueOrDefault(BuildPlayerWorldKey(playerId, worldId)));

    /// <inheritdoc />
    public Task SavePlayerWorldStateAsync(PlayerWorldState state, CancellationToken ct = default)
    {
        _playerWorldStates[BuildPlayerWorldKey(state.PlayerId, state.WorldId)] = state;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WorldStatSnapshot?> GetStatSnapshotAsync(string playerId, string worldId, CancellationToken ct = default)
        => Task.FromResult(_statSnapshots.GetValueOrDefault(BuildPlayerWorldKey(playerId, worldId)));

    /// <inheritdoc />
    public Task SaveStatSnapshotAsync(WorldStatSnapshot snapshot, CancellationToken ct = default)
    {
        _statSnapshots[BuildPlayerWorldKey(snapshot.PlayerId, snapshot.WorldId)] = snapshot;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<StatTranslationHistory?> GetTranslationHistoryAsync(string playerId, string sourceWorldId, string destinationWorldId, CancellationToken ct = default)
        => Task.FromResult(_translationHistory.GetValueOrDefault(BuildTranslationKey(playerId, sourceWorldId, destinationWorldId)));

    /// <inheritdoc />
    public Task SaveTranslationHistoryAsync(StatTranslationHistory history, CancellationToken ct = default)
    {
        _translationHistory[BuildTranslationKey(history.PlayerId, history.SourceWorldId, history.DestinationWorldId)] = history;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WorldNpcState?> GetWorldNpcStateAsync(string npcId, string worldId, string? playerId, CancellationToken ct = default)
        => Task.FromResult(_npcStates.GetValueOrDefault(BuildNpcKey(npcId, worldId, playerId)));

    /// <inheritdoc />
    public Task SaveWorldNpcStateAsync(WorldNpcState state, CancellationToken ct = default)
    {
        _npcStates[BuildNpcKey(state.NpcId, state.WorldId, state.PlayerId)] = state;
        return Task.CompletedTask;
    }

    private static string BuildPlayerWorldKey(string playerId, string worldId) => $"{playerId}:{worldId}";

    private static string BuildTranslationKey(string playerId, string sourceWorldId, string destinationWorldId)
        => $"{playerId}:{sourceWorldId}:{destinationWorldId}";

    private static string BuildNpcKey(string npcId, string worldId, string? playerId)
        => $"{npcId}:{worldId}:{playerId ?? "_global"}";
}