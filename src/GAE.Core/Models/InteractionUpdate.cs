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

    /// <summary>
    /// Memory flags to add to the NPC's permanent memory.
    /// Examples: "crime-witnessed", "romance", "friendship", "betrayal", "helped-in-battle"
    /// Flags starting with "!" are permanent and cannot be auto-removed.
    /// </summary>
    public List<string> MemoryFlags { get; set; } = [];

    /// <summary>
    /// Faction-wide mood shift (-100 to 100). Positive = faction becomes friendlier,
    /// negative = faction becomes more hostile. Applied to all same-faction NPCs in the room.
    /// </summary>
    public int FactionMoodShift { get; set; } = 0;
}
