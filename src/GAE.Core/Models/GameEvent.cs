namespace GAE.Core.Models;

/// <summary>
/// Stable envelope for all state mutations. Used for journal persistence,
/// SignalR broadcast, and dashboard updates.
/// </summary>
public class GameEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public string ActionId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public GameEventType Type { get; set; }
    public string PlayerId { get; set; } = string.Empty;
    public string? RoomId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? Narration { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public long SequenceNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public enum GameEventType
{
    PlayerCreated,
    PlayerMoved,
    PlayerAttacked,
    PlayerTalked,
    PlayerUsedItem,
    PlayerTookItem,
    PlayerDroppedItem,
    PlayerEquipped,
    PlayerUnequipped,
    PlayerRested,
    PlayerDied,
    PlayerRevived,
    CombatStarted,
    CombatEnded,
    CombatTurnAdvanced,
    RoomDiscovered,
    RoomUpdated,
    RoomDeleted,
    NpcSpawned,
    NpcDied,
    PlayerDeleted,
    StoryAdvanced,
    QuestUpdated,
    SystemMessage
}
