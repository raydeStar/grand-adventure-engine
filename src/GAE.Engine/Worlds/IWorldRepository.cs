using GAE.Engine.Worlds;

namespace GAE.Engine.Worlds;

/// <summary>
/// Persists multi-world metadata independently from the core gameplay state manager.
/// Phase one uses this repository to establish schema, defaults, and bootstrap behavior
/// without forcing world context into every existing gameplay API.
/// </summary>
public interface IWorldRepository
{
    /// <summary>Returns a world by ID, or null when it has not been created yet.</summary>
    Task<World?> GetWorldAsync(string worldId, CancellationToken ct = default);

    /// <summary>Returns all known worlds.</summary>
    Task<IReadOnlyList<World>> GetAllWorldsAsync(CancellationToken ct = default);

    /// <summary>Creates or updates a world definition.</summary>
    Task SaveWorldAsync(World world, CancellationToken ct = default);

    /// <summary>Deletes a world definition.</summary>
    Task<bool> RemoveWorldAsync(string worldId, CancellationToken ct = default);

    /// <summary>Returns a player's per-world location/state record.</summary>
    Task<PlayerWorldState?> GetPlayerWorldStateAsync(string playerId, string worldId, CancellationToken ct = default);

    /// <summary>Creates or updates a player's per-world location/state record.</summary>
    Task SavePlayerWorldStateAsync(PlayerWorldState state, CancellationToken ct = default);

    /// <summary>Returns a stat snapshot for a player in a specific world.</summary>
    Task<WorldStatSnapshot?> GetStatSnapshotAsync(string playerId, string worldId, CancellationToken ct = default);

    /// <summary>Creates or updates a stat snapshot for a player in a specific world.</summary>
    Task SaveStatSnapshotAsync(WorldStatSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Returns cached translation history for a player moving between two worlds.</summary>
    Task<StatTranslationHistory?> GetTranslationHistoryAsync(string playerId, string sourceWorldId, string destinationWorldId, CancellationToken ct = default);

    /// <summary>Creates or updates cached translation history for a player moving between two worlds.</summary>
    Task SaveTranslationHistoryAsync(StatTranslationHistory history, CancellationToken ct = default);

    /// <summary>Returns world-specific NPC state for an NPC/player combination.</summary>
    Task<WorldNpcState?> GetWorldNpcStateAsync(string npcId, string worldId, string? playerId, CancellationToken ct = default);

    /// <summary>Creates or updates world-specific NPC state for an NPC/player combination.</summary>
    Task SaveWorldNpcStateAsync(WorldNpcState state, CancellationToken ct = default);
}