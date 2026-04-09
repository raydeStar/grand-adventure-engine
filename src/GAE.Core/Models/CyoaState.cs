namespace GAE.Core.Models;

/// <summary>
/// Simplified player state for Choose Your Own Adventure mode.
/// No HP/MP/XP numbers — just a general health level, a flat string inventory,
/// and choice-tree navigation state.
/// </summary>
public class CyoaState
{
    /// <summary>General health state — intentionally vague, the narrator describes specifics.</summary>
    public CyoaHealthLevel Health { get; set; } = CyoaHealthLevel.Healthy;

    /// <summary>Flat list of narrative items (flags, not game objects). E.g. "Rusty Key", "Torch".</summary>
    public List<string> Inventory { get; set; } = [];

    /// <summary>ID of the current story node in the choice tree.</summary>
    public string CurrentNode { get; set; } = string.Empty;

    /// <summary>Node IDs that the player can rewind to.</summary>
    public List<string> SavePoints { get; set; } = [];

    /// <summary>Ordered history of choices the player has made.</summary>
    public List<CyoaChoiceRecord> ChoiceHistory { get; set; } = [];

    /// <summary>When this CYOA session started.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The room the player was in before starting the CYOA session (for restoration).</summary>
    public string PreviousRoomId { get; set; } = string.Empty;
}

/// <summary>
/// General health state for CYOA mode. No numbers — the narrator interprets these narratively.
/// </summary>
public enum CyoaHealthLevel
{
    Healthy,
    Hurt,
    Critical,
    Dead
}

/// <summary>
/// A single choice made during a CYOA session.
/// </summary>
public class CyoaChoiceRecord
{
    /// <summary>The node where the choice was made.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>The text of the choice the player selected.</summary>
    public string ChoiceText { get; set; } = string.Empty;

    /// <summary>When the choice was made.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
