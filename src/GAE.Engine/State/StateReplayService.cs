using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>
/// Orchestrates state recovery: loads checkpoint, replays journal,
/// rebuilds in-memory projections.
/// </summary>
public class StateReplayService : IStateReplayService
{
    private readonly InMemoryStateManager _stateManager;
    private readonly IStateJournal _journal;
    private readonly IStateCheckpointStore _checkpointStore;
    private readonly ILogger<StateReplayService> _logger;

    public StateReplayService(
        InMemoryStateManager stateManager,
        IStateJournal journal,
        IStateCheckpointStore checkpointStore,
        ILogger<StateReplayService> logger)
    {
        _stateManager = stateManager;
        _journal = journal;
        _checkpointStore = checkpointStore;
        _logger = logger;
    }

    public async Task ReplayAsync(CancellationToken ct = default)
    {
        long startSeq = 0;

        // 1. Load latest checkpoint
        var checkpoint = await _checkpointStore.LoadLatestCheckpointAsync(ct);
        if (checkpoint is not null)
        {
            var snapshot = JsonSerializer.Deserialize<StateSnapshot>(checkpoint.Payload);
            if (snapshot is not null)
            {
                _stateManager.RestoreFromSnapshot(snapshot);
                startSeq = checkpoint.SequenceNumber;
                _logger.LogInformation("Restored from checkpoint at seq={Seq}", startSeq);
            }
        }

        // 2. Replay journal events since checkpoint
        var events = await _journal.ReadFromAsync(startSeq, ct);
        _logger.LogInformation("Replaying {Count} journal events from seq={Seq}", events.Count, startSeq);

        foreach (var evt in events)
        {
            await ApplyEventAsync(evt, ct);
        }

        _logger.LogInformation("State replay complete. Current state has {Players} players, {Rooms} rooms",
            (await _stateManager.GetAllPlayersAsync(ct)).Count,
            (await _stateManager.GetAllRoomsAsync(ct)).Count);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        var snapshot = _stateManager.TakeSnapshot();
        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        var seq = await _journal.GetLatestSequenceNumberAsync(ct);

        var checkpoint = new StateCheckpoint
        {
            SequenceNumber = seq,
            Payload = payload
        };

        await _checkpointStore.SaveCheckpointAsync(checkpoint, ct);
        _logger.LogInformation("Flushed checkpoint at seq={Seq}", seq);
    }

    private async Task ApplyEventAsync(GameEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case GameEventType.PlayerCreated:
            case GameEventType.PlayerMoved:
            case GameEventType.PlayerAttacked:
            case GameEventType.PlayerUsedItem:
            case GameEventType.PlayerTookItem:
            case GameEventType.PlayerDroppedItem:
            case GameEventType.PlayerEquipped:
            case GameEventType.PlayerUnequipped:
            case GameEventType.PlayerRested:
            case GameEventType.PlayerDied:
            case GameEventType.PlayerRevived:
                await ReplayPlayerEventAsync(evt, ct);
                break;

            case GameEventType.RoomDiscovered:
            case GameEventType.RoomUpdated:
                await ReplayRoomEventAsync(evt, ct);
                break;

            case GameEventType.StoryAdvanced:
                await ReplayStoryEventAsync(evt, ct);
                break;

            case GameEventType.CombatStarted:
            case GameEventType.CombatEnded:
            case GameEventType.CombatTurnAdvanced:
                // Combat state is transient; skip replay for ended combats
                break;

            default:
                _logger.LogDebug("Skipping replay of event type {Type}", evt.Type);
                break;
        }
    }

    private async Task ReplayPlayerEventAsync(GameEvent evt, CancellationToken ct)
    {
        if (evt.Data.TryGetValue("player", out var playerObj) && playerObj is JsonElement element)
        {
            var player = element.Deserialize<PlayerCharacter>();
            if (player is not null)
                await _stateManager.SavePlayerAsync(player, ct);
        }
    }

    private async Task ReplayRoomEventAsync(GameEvent evt, CancellationToken ct)
    {
        if (evt.Data.TryGetValue("room", out var roomObj) && roomObj is JsonElement element)
        {
            var room = element.Deserialize<Room>();
            if (room is not null)
                await _stateManager.SaveRoomAsync(room, ct);
        }
    }

    private async Task ReplayStoryEventAsync(GameEvent evt, CancellationToken ct)
    {
        if (evt.Data.TryGetValue("story", out var storyObj) && storyObj is JsonElement element)
        {
            var entry = element.Deserialize<StoryEntry>();
            if (entry is not null)
                await _stateManager.AddStoryEntryAsync(entry, ct);
        }
    }
}
