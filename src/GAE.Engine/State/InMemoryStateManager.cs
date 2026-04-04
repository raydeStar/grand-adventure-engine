using System.Collections.Concurrent;
using GAE.Core.Interfaces;
using GAE.Core.Models;

namespace GAE.Engine.State;

/// <summary>
/// In-memory projection of game state. All mutations go through this manager
/// and are journaled for durability. On restart, state is rebuilt from
/// the latest checkpoint + journal replay.
/// </summary>
public class InMemoryStateManager : IStateManager
{
    private readonly ConcurrentDictionary<string, PlayerCharacter> _players = new();
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, Room> _playerRooms = new();
    private readonly ConcurrentDictionary<string, CombatState> _combats = new();
    private readonly List<StoryEntry> _storyEntries = [];
    private readonly Lock _storyLock = new();

    // Player operations
    public Task<PlayerCharacter?> GetPlayerAsync(string playerId, CancellationToken ct = default)
        => Task.FromResult(_players.GetValueOrDefault(playerId));

    public Task<PlayerCharacter?> GetPlayerByDiscordIdAsync(string discordId, CancellationToken ct = default)
        => Task.FromResult(_players.Values.FirstOrDefault(p => p.DiscordId == discordId));

    public Task<IReadOnlyList<PlayerCharacter>> GetAllPlayersAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlayerCharacter>>(_players.Values.ToList());

    public Task SavePlayerAsync(PlayerCharacter player, CancellationToken ct = default)
    {
        _players[player.Id] = player;
        return Task.CompletedTask;
    }

    public Task<bool> RemovePlayerAsync(string playerId, CancellationToken ct = default)
        => Task.FromResult(_players.TryRemove(playerId, out _));

    // Room operations (templates)
    public Task<Room?> GetRoomAsync(string roomId, CancellationToken ct = default)
        => Task.FromResult(_rooms.GetValueOrDefault(roomId));

    public Task<IReadOnlyList<Room>> GetAllRoomsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Room>>(_rooms.Values.ToList());

    public Task SaveRoomAsync(Room room, CancellationToken ct = default)
    {
        // Player room instances have IDs in the format "playerId:roomId"
        if (room.Id.Contains(':'))
            _playerRooms[room.Id] = room;
        else
            _rooms[room.Id] = room;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveRoomAsync(string roomId, CancellationToken ct = default)
        => Task.FromResult(_rooms.TryRemove(roomId, out _));

    public Task RemoveAllRoomsAsync(CancellationToken ct = default)
    {
        _rooms.Clear();
        _playerRooms.Clear();
        return Task.CompletedTask;
    }

    // Per-player room instances
    public Task<Room?> GetPlayerRoomAsync(string playerId, string roomId, CancellationToken ct = default)
    {
        var key = $"{playerId}:{roomId}";
        if (_playerRooms.TryGetValue(key, out var existing))
            return Task.FromResult<Room?>(existing);

        // Clone from template
        if (!_rooms.TryGetValue(roomId, out var template))
            return Task.FromResult<Room?>(null);

        var clone = template.DeepClone(key);
        clone.IsDiscovered = true;
        clone.DiscoveredAt = DateTimeOffset.UtcNow;
        _playerRooms[key] = clone;
        return Task.FromResult<Room?>(clone);
    }

    public Task RemovePlayerRoomsAsync(string playerId, CancellationToken ct = default)
    {
        var prefix = $"{playerId}:";
        var keysToRemove = _playerRooms.Keys.Where(k => k.StartsWith(prefix)).ToList();
        foreach (var key in keysToRemove)
            _playerRooms.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    // Story operations
    public Task AddStoryEntryAsync(StoryEntry entry, CancellationToken ct = default)
    {
        lock (_storyLock)
        {
            _storyEntries.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoryEntry>> GetStoryEntriesAsync(string? playerId = null, int limit = 50, CancellationToken ct = default)
    {
        lock (_storyLock)
        {
            IEnumerable<StoryEntry> query = _storyEntries.AsEnumerable().Reverse();
            if (playerId is not null)
                query = query.Where(e => e.PlayerId == playerId);
            return Task.FromResult<IReadOnlyList<StoryEntry>>(query.Take(limit).ToList());
        }
    }

    public Task<IReadOnlyList<StoryEntry>> GetRecentStoryForRoomAsync(string roomId, int limit = 10, CancellationToken ct = default)
    {
        lock (_storyLock)
        {
            var entries = _storyEntries.AsEnumerable().Reverse()
                .Where(e => e.RoomId == roomId)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<StoryEntry>>(entries);
        }
    }

    public Task ClearStoryAsync(CancellationToken ct = default)
    {
        lock (_storyLock)
        {
            _storyEntries.Clear();
        }
        return Task.CompletedTask;
    }

    // Combat operations
    public Task<CombatState?> GetCombatStateAsync(string roomId, CancellationToken ct = default)
        => Task.FromResult(_combats.GetValueOrDefault(roomId));

    public Task SaveCombatStateAsync(CombatState combat, CancellationToken ct = default)
    {
        _combats[combat.RoomId] = combat;
        return Task.CompletedTask;
    }

    public Task RemoveCombatStateAsync(string roomId, CancellationToken ct = default)
    {
        _combats.TryRemove(roomId, out _);
        return Task.CompletedTask;
    }

    public Task RemoveAllCombatStatesAsync(CancellationToken ct = default)
    {
        _combats.Clear();
        return Task.CompletedTask;
    }

    // Snapshot support for checkpointing
    public StateSnapshot TakeSnapshot()
    {
        lock (_storyLock)
        {
            return new StateSnapshot
            {
                Players = _players.Values.ToList(),
                Rooms = _rooms.Values.ToList(),
                PlayerRooms = _playerRooms.Values.ToList(),
                StoryEntries = [.. _storyEntries],
                CombatStates = _combats.Values.ToList()
            };
        }
    }

    public void RestoreFromSnapshot(StateSnapshot snapshot)
    {
        _players.Clear();
        foreach (var p in snapshot.Players)
            _players[p.Id] = p;

        _rooms.Clear();
        foreach (var r in snapshot.Rooms)
            _rooms[r.Id] = r;

        _playerRooms.Clear();
        foreach (var r in snapshot.PlayerRooms)
            _playerRooms[r.Id] = r;

        _combats.Clear();
        foreach (var c in snapshot.CombatStates)
            _combats[c.RoomId] = c;

        lock (_storyLock)
        {
            _storyEntries.Clear();
            _storyEntries.AddRange(snapshot.StoryEntries);
        }
    }
}

public class StateSnapshot
{
    public List<PlayerCharacter> Players { get; set; } = [];
    public List<Room> Rooms { get; set; } = [];
    public List<Room> PlayerRooms { get; set; } = [];
    public List<StoryEntry> StoryEntries { get; set; } = [];
    public List<CombatState> CombatStates { get; set; } = [];
}