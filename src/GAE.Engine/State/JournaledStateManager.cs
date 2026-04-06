using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>
/// Wraps the in-memory state manager to journal every mutation.
/// This is the single-writer entry point for all state changes.
/// </summary>
public class JournaledStateManager : IStateManager
{
    private readonly InMemoryStateManager _inner;
    private readonly IStateJournal _journal;
    private readonly ILogger<JournaledStateManager> _logger;

    public JournaledStateManager(
        InMemoryStateManager inner,
        IStateJournal journal,
        ILogger<JournaledStateManager> logger)
    {
        _inner = inner;
        _journal = journal;
        _logger = logger;
    }

    // Read operations delegate directly
    public Task<PlayerCharacter?> GetPlayerAsync(string playerId, CancellationToken ct = default)
        => _inner.GetPlayerAsync(playerId, ct);

    public Task<PlayerCharacter?> GetPlayerByDiscordIdAsync(string discordId, CancellationToken ct = default)
        => _inner.GetPlayerByDiscordIdAsync(discordId, ct);

    public Task<IReadOnlyList<PlayerCharacter>> GetAllPlayersAsync(CancellationToken ct = default)
        => _inner.GetAllPlayersAsync(ct);

    public Task<Room?> GetRoomAsync(string roomId, CancellationToken ct = default)
        => _inner.GetRoomAsync(roomId, ct);

    public Task<IReadOnlyList<Room>> GetAllRoomsAsync(CancellationToken ct = default)
        => _inner.GetAllRoomsAsync(ct);

    public Task<IReadOnlyList<StoryEntry>> GetStoryEntriesAsync(string? playerId = null, int limit = 50, CancellationToken ct = default)
        => _inner.GetStoryEntriesAsync(playerId, limit, ct);

    public Task<IReadOnlyList<StoryEntry>> GetRecentStoryForRoomAsync(string roomId, int limit = 10, CancellationToken ct = default)
        => _inner.GetRecentStoryForRoomAsync(roomId, limit, ct);

    public Task<CombatState?> GetCombatStateAsync(string roomId, CancellationToken ct = default)
        => _inner.GetCombatStateAsync(roomId, ct);

    // Write operations journal then apply
    public async Task SavePlayerAsync(PlayerCharacter player, CancellationToken ct = default)
    {
        var existing = await _inner.GetPlayerAsync(player.Id, ct);
        var eventType = existing is null ? GameEventType.PlayerCreated : GameEventType.PlayerMoved;

        await _inner.SavePlayerAsync(player, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = eventType,
            PlayerId = player.Id,
            RoomId = player.CurrentRoomId,
            Summary = $"Player {player.Name} saved",
            Data = new Dictionary<string, object?> { ["player"] = player }
        }, ct);
    }

    public async Task<bool> RemovePlayerAsync(string playerId, CancellationToken ct = default)
    {
        var player = await _inner.GetPlayerAsync(playerId, ct);
        var removed = await _inner.RemovePlayerAsync(playerId, ct);
        if (removed)
        {
            await _journal.AppendAsync(new GameEvent
            {
                Type = GameEventType.PlayerDeleted,
                PlayerId = playerId,
                Summary = $"Player {player?.Name ?? playerId} deleted"
            }, ct);
        }
        return removed;
    }

    public async Task SaveRoomAsync(Room room, CancellationToken ct = default)
    {
        var existing = await _inner.GetRoomAsync(room.Id, ct);
        var eventType = existing is null ? GameEventType.RoomDiscovered : GameEventType.RoomUpdated;

        await _inner.SaveRoomAsync(room, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = eventType,
            RoomId = room.Id,
            Summary = $"Room {room.Name} saved",
            Data = new Dictionary<string, object?> { ["room"] = room }
        }, ct);
    }

    public async Task AddStoryEntryAsync(StoryEntry entry, CancellationToken ct = default)
    {
        await _inner.AddStoryEntryAsync(entry, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.StoryAdvanced,
            PlayerId = entry.PlayerId,
            RoomId = entry.RoomId,
            Summary = entry.MechanicalSummary,
            Narration = entry.Narration,
            Data = new Dictionary<string, object?> { ["story"] = entry }
        }, ct);
    }

    public async Task SaveCombatStateAsync(CombatState combat, CancellationToken ct = default)
    {
        await _inner.SaveCombatStateAsync(combat, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = combat.IsActive ? GameEventType.CombatStarted : GameEventType.CombatEnded,
            RoomId = combat.RoomId,
            Summary = $"Combat in {combat.RoomId} — round {combat.RoundNumber}"
        }, ct);
    }

    public async Task RemoveCombatStateAsync(string roomId, CancellationToken ct = default)
    {
        await _inner.RemoveCombatStateAsync(roomId, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.CombatEnded,
            RoomId = roomId,
            Summary = $"Combat ended in {roomId}"
        }, ct);
    }

    public async Task<bool> RemoveRoomAsync(string roomId, CancellationToken ct = default)
    {
        var removed = await _inner.RemoveRoomAsync(roomId, ct);
        if (removed)
        {
            await _journal.AppendAsync(new GameEvent
            {
                Type = GameEventType.RoomDeleted,
                RoomId = roomId,
                Summary = $"Room {roomId} deleted"
            }, ct);
        }
        return removed;
    }

    public async Task RemoveAllRoomsAsync(CancellationToken ct = default)
    {
        await _inner.RemoveAllRoomsAsync(ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            Summary = "All rooms removed (world reset)"
        }, ct);
    }

    public async Task ClearStoryAsync(CancellationToken ct = default)
    {
        await _inner.ClearStoryAsync(ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            Summary = "Story log cleared (world reset)"
        }, ct);
    }

    public async Task RemoveAllCombatStatesAsync(CancellationToken ct = default)
    {
        await _inner.RemoveAllCombatStatesAsync(ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            Summary = "All combat states removed (world reset)"
        }, ct);
    }

    public Task<PartyQuestProgress?> GetPartyQuestAsync(string groupId, CancellationToken ct = default)
        => _inner.GetPartyQuestAsync(groupId, ct);

    public async Task SavePartyQuestAsync(PartyQuestProgress progress, CancellationToken ct = default)
    {
        await _inner.SavePartyQuestAsync(progress, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            Summary = $"Party quest {progress.QuestId} saved for group {progress.GroupId}",
            Data = new Dictionary<string, object?> { ["partyQuest"] = progress }
        }, ct);
    }

    public async Task RemovePartyQuestAsync(string groupId, CancellationToken ct = default)
    {
        await _inner.RemovePartyQuestAsync(groupId, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            Summary = $"Party quest group {groupId} removed"
        }, ct);
    }

    // Per-player room instances
    public async Task<Room?> GetPlayerRoomAsync(string playerId, string roomId, CancellationToken ct = default)
    {
        var existing = await _inner.GetPlayerRoomAsync(playerId, roomId, ct);
        return existing;
    }

    public async Task RemovePlayerRoomsAsync(string playerId, CancellationToken ct = default)
    {
        await _inner.RemovePlayerRoomsAsync(playerId, ct);
        await _journal.AppendAsync(new GameEvent
        {
            Type = GameEventType.SystemMessage,
            PlayerId = playerId,
            Summary = $"All player room instances removed for {playerId}"
        }, ct);
    }
}
