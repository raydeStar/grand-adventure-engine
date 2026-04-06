namespace GAE.Core.Models;

public class FreeFormResponse
{
    public string Narration { get; set; } = string.Empty;
    public bool Success { get; set; } = true;
    public Dictionary<string, int> StatChanges { get; set; } = new();
    public List<InventoryChange> InventoryChanges { get; set; } = [];
    public List<EntityChange> EntityChanges { get; set; } = [];
    public bool CombatInitiated { get; set; }
    public RoomChange? RoomChanges { get; set; }
    public InteractionUpdate? InteractionUpdate { get; set; }
    public QuestUpdates? QuestUpdates { get; set; }
}

public class InventoryChange
{
    public string Action { get; set; } = string.Empty; // "add" or "remove"
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

public class EntityChange
{
    public string EntityType { get; set; } = string.Empty; // "npc" or "item"
    public string Action { get; set; } = string.Empty; // "add", "remove", "update"
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class RoomChange
{
    public string? NewDescription { get; set; }
    public Dictionary<string, string>? NewExits { get; set; }
}
