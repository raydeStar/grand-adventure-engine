using GAE.Core.Models;

namespace GAE.Core.Interfaces;

public interface IStateManager
{
    // Player operations
    Task<PlayerCharacter?> GetPlayerAsync(string playerId, CancellationToken ct = default);
    Task<PlayerCharacter?> GetPlayerByDiscordIdAsync(string discordId, CancellationToken ct = default);
    Task<IReadOnlyList<PlayerCharacter>> GetAllPlayersAsync(CancellationToken ct = default);
    Task SavePlayerAsync(PlayerCharacter player, CancellationToken ct = default);
    Task<bool> RemovePlayerAsync(string playerId, CancellationToken ct = default);

    // Room operations (templates)
    Task<Room?> GetRoomAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<Room>> GetAllRoomsAsync(CancellationToken ct = default);
    Task SaveRoomAsync(Room room, CancellationToken ct = default);
    Task<bool> RemoveRoomAsync(string roomId, CancellationToken ct = default);
    Task RemoveAllRoomsAsync(CancellationToken ct = default);

    // Per-player room instances
    /// <summary>
    /// Returns the player's personal copy of a room. On first call for a player+room combo,
    /// clones the template room and saves it. Subsequent calls return the existing instance.
    /// </summary>
    Task<Room?> GetPlayerRoomAsync(string playerId, string roomId, CancellationToken ct = default);

    /// <summary>Deletes all per-player room instances for a specific player (used on restart/reset).</summary>
    Task RemovePlayerRoomsAsync(string playerId, CancellationToken ct = default);

    // Story operations
    Task AddStoryEntryAsync(StoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<StoryEntry>> GetStoryEntriesAsync(string? playerId = null, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<StoryEntry>> GetRecentStoryForRoomAsync(string roomId, int limit = 10, CancellationToken ct = default);

    // Story bulk operations
    Task ClearStoryAsync(CancellationToken ct = default);

    // Combat operations
    Task<CombatState?> GetCombatStateAsync(string roomId, CancellationToken ct = default);
    Task SaveCombatStateAsync(CombatState combat, CancellationToken ct = default);
    Task RemoveCombatStateAsync(string roomId, CancellationToken ct = default);
    Task RemoveAllCombatStatesAsync(CancellationToken ct = default);

    // Party quest operations
    Task<PartyQuestProgress?> GetPartyQuestAsync(string groupId, CancellationToken ct = default);
    Task SavePartyQuestAsync(PartyQuestProgress progress, CancellationToken ct = default);
    Task RemovePartyQuestAsync(string groupId, CancellationToken ct = default);
}