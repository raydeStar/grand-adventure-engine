using System.Text;
using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

/// <summary>
/// Manages lore discovery for players. Handles cascade logic, starter lore,
/// and tree-view rendering for the lorebook command.
/// </summary>
public class LoreTracker
{
    private readonly IContentRegistryService _registry;
    private readonly ILogger<LoreTracker> _logger;

    public LoreTracker(IContentRegistryService registry, ILogger<LoreTracker> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════
    //  DISCOVERY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to discover lore entries triggered by a specific source entity (NPC, room, quest, item).
    /// Returns a list of newly discovered lore entry names for display.
    /// </summary>
    public List<string> DiscoverBySource(PlayerCharacter player, string sourceEntityId, string triggerType, string? worldId = null)
    {
        var discovered = new List<string>();
        var entries = _registry.LoreEntries.GetAll()
            .Where(l => MatchesTrigger(l, triggerType) &&
                        string.Equals(l.DiscoverySourceId, sourceEntityId, StringComparison.OrdinalIgnoreCase) &&
                        InWorld(l, worldId));

        foreach (var entry in entries)
        {
            var newlyFound = DiscoverEntry(player, entry);
            discovered.AddRange(newlyFound);
        }

        return discovered;
    }

    /// <summary>
    /// Discover a specific lore entry by ID. Handles cascade logic.
    /// Returns list of all newly discovered entry names (including cascaded children).
    /// </summary>
    public List<string> DiscoverById(PlayerCharacter player, string loreId)
    {
        var entry = _registry.LoreEntries.GetById(loreId);
        if (entry is null) return [];
        return DiscoverEntry(player, entry);
    }

    /// <summary>
    /// Grant all starter lore entries for a given world to the player.
    /// Called on character creation.
    /// </summary>
    public List<string> GrantStarterLore(PlayerCharacter player, string? worldId = null)
    {
        var discovered = new List<string>();
        var starterEntries = _registry.LoreEntries.GetAll()
            .Where(l => l.IsStarterLore && InWorld(l, worldId));

        foreach (var entry in starterEntries)
        {
            if (!player.DiscoveredLore.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
            {
                player.DiscoveredLore.Add(entry.Id);
                discovered.Add(entry.Name);
                _logger.LogDebug("Granted starter lore '{LoreId}' to player {PlayerId}", entry.Id, player.Id);

                // Cascade if applicable
                if (entry.CascadeDown)
                    discovered.AddRange(CascadeChildren(player, entry.Id));
            }
        }

        return discovered;
    }

    /// <summary>
    /// Check if a player has discovered a specific lore entry.
    /// </summary>
    public bool HasDiscovered(PlayerCharacter player, string loreId)
        => player.DiscoveredLore.Contains(loreId, StringComparer.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════════════════════════
    //  LOREBOOK DISPLAY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Format the lorebook as a hierarchical tree for display.
    /// Shows discovered entries with full info and undiscovered children as teasers.
    /// </summary>
    public string FormatLorebook(PlayerCharacter player, string? worldId = null)
    {
        var allEntries = _registry.LoreEntries.GetAll()
            .Where(l => InWorld(l, worldId))
            .ToList();

        if (allEntries.Count == 0)
            return "📖 Your lorebook is empty. Explore the world to discover its secrets.";

        var discovered = new HashSet<string>(player.DiscoveredLore, StringComparer.OrdinalIgnoreCase);
        if (discovered.Count == 0)
            return "📖 Your lorebook is empty. Explore the world to discover its secrets.";

        // Build parent→children lookup
        var childrenOf = allEntries
            .Where(l => l.ParentLoreId is not null)
            .GroupBy(l => l.ParentLoreId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.LoreScope).ThenBy(l => l.Name).ToList(), StringComparer.OrdinalIgnoreCase);

        // Find root entries (no parent) that are discovered or have discovered descendants
        var roots = allEntries
            .Where(l => l.ParentLoreId is null && HasDiscoveredDescendant(l.Id, discovered, childrenOf))
            .OrderBy(l => l.LoreScope == "mechanic" ? 0 : 1)
            .ThenBy(l => l.Name)
            .ToList();

        var sb = new StringBuilder();
        var totalDiscovered = allEntries.Count(l => discovered.Contains(l.Id));
        sb.AppendLine($"📖 **LOREBOOK** — {totalDiscovered}/{allEntries.Count} entries discovered");
        sb.AppendLine();

        foreach (var root in roots)
            RenderTreeNode(sb, root, discovered, childrenOf, indent: 0);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Format a specific lore entry's full content for display.
    /// Used when the player types "lore <name>" to read details.
    /// </summary>
    public string FormatLoreEntry(PlayerCharacter player, string query)
    {
        // Find by name or ID (fuzzy)
        var entry = _registry.LoreEntries.GetById(query)
            ?? _registry.LoreEntries.FindByName(query);

        if (entry is null)
        {
            // Try partial match
            entry = _registry.LoreEntries.GetAll()
                .FirstOrDefault(l => l.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || l.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is null)
            return $"No lore entry found matching \"{query}\".";

        if (!HasDiscovered(player, entry.Id))
            return entry.Teaser ?? "You haven't discovered this lore yet. Keep exploring!";

        var sb = new StringBuilder();
        sb.AppendLine($"📖 **{entry.Name}** [{entry.LoreScope}]");
        if (!string.IsNullOrWhiteSpace(entry.Description))
            sb.AppendLine($"*{entry.Description}*");
        sb.AppendLine();
        sb.AppendLine(entry.Content ?? entry.Description ?? "No details available.");

        // Show children summary
        var children = _registry.LoreEntries.GetAll()
            .Where(l => string.Equals(l.ParentLoreId, entry.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (children.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Related:**");
            foreach (var child in children.OrderBy(c => c.Name))
            {
                var known = HasDiscovered(player, child.Id);
                sb.AppendLine(known
                    ? $"  ◆ {child.Name} — {child.Description}"
                    : $"  ◇ {child.Teaser ?? "???"}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ═══════════════════════════════════════════════════════════════
    //  LORE DISCOVERY NOTIFICATION FORMATTING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Format lore discovery notifications for appending to action results.
    /// Returns null if no lore was discovered.
    /// </summary>
    public static string? FormatDiscoveryNotice(List<string> discoveredNames)
    {
        if (discoveredNames.Count == 0) return null;
        if (discoveredNames.Count == 1)
            return $"📖 [Lore Discovered: {discoveredNames[0]}]";
        return $"📖 [Lore Discovered: {string.Join(", ", discoveredNames)}]";
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════════════════════════════

    private List<string> DiscoverEntry(PlayerCharacter player, LoreEntry entry)
    {
        var discovered = new List<string>();

        if (player.DiscoveredLore.Contains(entry.Id, StringComparer.OrdinalIgnoreCase))
            return discovered; // Already known

        player.DiscoveredLore.Add(entry.Id);
        discovered.Add(entry.Name);
        _logger.LogInformation("Player {PlayerId} discovered lore: {LoreId} ({LoreName})", player.Id, entry.Id, entry.Name);

        // Cascade to children if enabled
        if (entry.CascadeDown)
            discovered.AddRange(CascadeChildren(player, entry.Id));

        return discovered;
    }

    private List<string> CascadeChildren(PlayerCharacter player, string parentId)
    {
        var discovered = new List<string>();
        var children = _registry.LoreEntries.GetAll()
            .Where(l => string.Equals(l.ParentLoreId, parentId, StringComparison.OrdinalIgnoreCase));

        foreach (var child in children)
        {
            if (!player.DiscoveredLore.Contains(child.Id, StringComparer.OrdinalIgnoreCase))
            {
                player.DiscoveredLore.Add(child.Id);
                discovered.Add(child.Name);
                _logger.LogDebug("Cascade discovered lore '{LoreId}' via parent '{ParentId}'", child.Id, parentId);

                // Recursive cascade
                if (child.CascadeDown)
                    discovered.AddRange(CascadeChildren(player, child.Id));
            }
        }

        return discovered;
    }

    private static bool MatchesTrigger(LoreEntry entry, string triggerType)
    {
        // DiscoveryTrigger can be comma-separated: "talk,explore"
        return entry.DiscoveryTrigger
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => string.Equals(t, triggerType, StringComparison.OrdinalIgnoreCase));
    }

    private static bool InWorld(LoreEntry entry, string? worldId)
        => worldId is null || entry.WorldIds.Contains(worldId, StringComparer.OrdinalIgnoreCase);

    private void RenderTreeNode(StringBuilder sb, LoreEntry entry, HashSet<string> discovered,
        Dictionary<string, List<LoreEntry>> childrenOf, int indent)
    {
        var pad = new string(' ', indent * 2);
        var isKnown = discovered.Contains(entry.Id);
        var scopeIcon = GetScopeIcon(entry.LoreScope);

        if (isKnown)
        {
            sb.AppendLine($"{pad}{scopeIcon} **{entry.Name}** — {entry.Description ?? ""}");
        }
        else
        {
            // Show teaser for undiscovered entries that are visible because a parent is discovered
            var teaser = entry.Teaser ?? "???";
            sb.AppendLine($"{pad}◇ *{teaser}*");
            return; // Don't show children of undiscovered nodes
        }

        // Render children
        if (childrenOf.TryGetValue(entry.Id, out var children))
        {
            foreach (var child in children)
            {
                if (discovered.Contains(child.Id) || ParentIsDiscovered(child, discovered))
                    RenderTreeNode(sb, child, discovered, childrenOf, indent + 1);
            }
        }
    }

    private bool ParentIsDiscovered(LoreEntry entry, HashSet<string> discovered)
        => entry.ParentLoreId is not null && discovered.Contains(entry.ParentLoreId);

    private bool HasDiscoveredDescendant(string entryId, HashSet<string> discovered, Dictionary<string, List<LoreEntry>> childrenOf)
    {
        if (discovered.Contains(entryId)) return true;
        if (!childrenOf.TryGetValue(entryId, out var children)) return false;
        return children.Any(c => HasDiscoveredDescendant(c.Id, discovered, childrenOf));
    }

    private static string GetScopeIcon(string scope) => scope.ToLowerInvariant() switch
    {
        "planet" => "🌍",
        "region" => "🗺️",
        "city" => "🏛️",
        "faction" => "⚔️",
        "npc" => "👤",
        "location" => "📍",
        "item" => "🔮",
        "event" => "⚡",
        "mechanic" => "⚙️",
        "history" => "📜",
        _ => "◆"
    };
}
