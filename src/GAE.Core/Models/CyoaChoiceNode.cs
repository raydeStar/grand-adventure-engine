namespace GAE.Core.Models;

/// <summary>
/// A single node in a CYOA choice tree. Each node has narration text and
/// 2-4 choices the player can pick from. Nodes are generated on-the-fly
/// by the narrator, not pre-authored.
/// </summary>
public class CyoaChoiceNode
{
    /// <summary>Unique identifier for this node (e.g. "chapter-2-the-bridge").</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>The narrator's description of the current scene.</summary>
    public string NarrationText { get; set; } = string.Empty;

    /// <summary>The choices available to the player at this node (2-4).</summary>
    public List<CyoaChoice> Choices { get; set; } = [];

    /// <summary>ID of the node that led here, or null for the first node.</summary>
    public string? ParentNodeId { get; set; }

    /// <summary>Whether this node is a save point the player can rewind to.</summary>
    public bool IsSavePoint { get; set; }

    /// <summary>Optional health change signaled by the narrator (e.g. "worse", "hurt").</summary>
    public string? HealthChange { get; set; }

    /// <summary>Items the player gains at this node.</summary>
    public List<string> ItemsGained { get; set; } = [];

    /// <summary>Items the player loses at this node.</summary>
    public List<string> ItemsLost { get; set; } = [];

    /// <summary>
    /// Ending signal from the narrator — null if the story continues.
    /// Values: "victory", "tragedy", "cliffhanger", "open".
    /// </summary>
    public string? Ending { get; set; }
}

/// <summary>
/// A single choice within a CYOA node.
/// </summary>
public class CyoaChoice
{
    /// <summary>Short identifier for this choice (e.g. "cross-carefully").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display text shown to the player.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Items required to see this choice. Empty = always visible.</summary>
    public List<string> RequiredItems { get; set; } = [];
}
