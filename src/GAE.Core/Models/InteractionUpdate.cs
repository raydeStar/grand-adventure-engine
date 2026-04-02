namespace GAE.Core.Models;

/// <summary>
/// Returned by the AI or engine to update the player's interaction state
/// after processing a turn in conversation, combat, etc.
/// </summary>
public class InteractionUpdate
{
    public InteractionMode Mode { get; set; } = InteractionMode.Explore;
    public string? NpcDisposition { get; set; }
    public NpcDispositionState? DispositionState { get; set; }
    public List<string> Context { get; set; } = [];
    public string? CombatStatus { get; set; }
    public List<InventoryItem> Loot { get; set; } = [];
    public Dictionary<string, int> EnemyUpdate { get; set; } = new();
}
