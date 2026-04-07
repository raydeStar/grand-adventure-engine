using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// A discoverable lore entry in the world knowledge hierarchy.
/// Lore forms a tree: Planet → Region → City → Faction → NPC/Location/Item/Event.
/// Discovering a parent can cascade discovery of children depending on <see cref="CascadeDown"/>.
/// </summary>
public class LoreEntry : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>Parent lore entry ID. Null = root node (e.g. a planet).</summary>
    public string? ParentLoreId { get; set; }

    /// <summary>
    /// Scope/category of this lore entry in the hierarchy.
    /// Values: planet, region, city, faction, npc, location, item, event, mechanic, history, custom.
    /// </summary>
    public string LoreScope { get; set; } = "custom";

    /// <summary>
    /// When true, discovering this entry also reveals all children.
    /// When false, children show as "???" teasers until individually discovered.
    /// </summary>
    public bool CascadeDown { get; set; }

    /// <summary>
    /// The full lore text shown when the player has discovered this entry.
    /// Supports multi-paragraph content for deep lore.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// Short teaser shown when the player knows about the parent but hasn't discovered this entry yet.
    /// Example: "A secretive resistance group operates in the shadows..."
    /// </summary>
    public string? Teaser { get; set; }

    /// <summary>
    /// Whether this lore is automatically granted to new characters in this world.
    /// Used for basic world mechanics, stat explanations, starting location info.
    /// </summary>
    public bool IsStarterLore { get; set; }

    /// <summary>
    /// How this lore can be discovered: talk (NPC conversation), explore (room visit),
    /// quest (quest completion), item (item pickup), combat (defeat enemy), auto (always known).
    /// Multiple triggers can be comma-separated.
    /// </summary>
    public string DiscoveryTrigger { get; set; } = "talk";

    /// <summary>
    /// Optional: the specific entity ID that triggers discovery (NPC ID, room ID, quest ID, item ID).
    /// If null, discovery is manual/scripted.
    /// </summary>
    public string? DiscoverySourceId { get; set; }

    /// <summary>
    /// Optional: linked entity IDs that this lore is about (NPC IDs, room IDs, etc.).
    /// Used for contextual lookups — e.g. when talking to an NPC, find lore about them.
    /// </summary>
    public List<string> LinkedEntityIds { get; set; } = [];

    /// <summary>
    /// Narrator hint: injected into AI prompts when this lore is relevant to the current scene.
    /// Guides the narrator on how to weave this knowledge into dialogue/narration.
    /// </summary>
    public string? NarratorHint { get; set; }

    /// <summary>Tags for search/filter (e.g. "ff7", "resistance", "backstory").</summary>
    public List<string> Tags { get; set; } = [];
}
