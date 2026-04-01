namespace GAE.Core.Models;

public class ActionResult
{
    public string ActionId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public bool Success { get; set; }
    public string MechanicalSummary { get; set; } = string.Empty;
    public string? Narration { get; set; }
    public List<DiceRoll> DiceRolls { get; set; } = [];
    public List<StateChange> StateChanges { get; set; } = [];
    public CombatState? CombatUpdate { get; set; }
    public Room? NewRoom { get; set; }
    public List<InventoryItem> ItemsGained { get; set; } = [];
    public List<InventoryItem> ItemsLost { get; set; } = [];
    public int GoldChange { get; set; }
    public int XpGained { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public class StateChange
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Property { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
