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

    // Room operations
    Task<Room?> GetRoomAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<Room>> GetAllRoomsAsync(CancellationToken ct = default);
    Task SaveRoomAsync(Room room, CancellationToken ct = default);

    // Story operations
    Task AddStoryEntryAsync(StoryEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<StoryEntry>> GetStoryEntriesAsync(string? playerId = null, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<StoryEntry>> GetRecentStoryForRoomAsync(string roomId, int limit = 10, CancellationToken ct = default);

    // Combat operations
    Task<CombatState?> GetCombatStateAsync(string roomId, CancellationToken ct = default);
    Task SaveCombatStateAsync(CombatState combat, CancellationToken ct = default);
    Task RemoveCombatStateAsync(string roomId, CancellationToken ct = default);
}
