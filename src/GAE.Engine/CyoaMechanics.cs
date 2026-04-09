using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

/// <summary>
/// Handles CYOA-specific mechanics: health transitions, inventory management,
/// and applying structured narrator results to player state.
/// </summary>
public static class CyoaMechanics
{
    /// <summary>
    /// Applies a health change to a CYOA player. Enforces valid transitions
    /// but allows skipping levels (e.g. Healthy → Critical for dramatic moments).
    /// Returns a flavor-text description of the new health state.
    /// </summary>
    public static string? ApplyHealthChange(CyoaState state, string change, ILogger? logger = null)
    {
        if (state.Health == CyoaHealthLevel.Dead)
        {
            logger?.LogWarning("Attempted health change on dead CYOA player: {Change}", change);
            return null;
        }

        var newHealth = ParseHealthChange(state.Health, change);
        if (newHealth == state.Health) return null;

        var oldHealth = state.Health;
        state.Health = newHealth;

        logger?.LogInformation("CYOA health transition: {Old} → {New} (signal: {Change})", oldHealth, newHealth, change);

        return DescribeHealth(newHealth);
    }

    /// <summary>
    /// Adds items to the CYOA inventory. Ignores duplicates.
    /// </summary>
    public static List<string> AddItems(CyoaState state, IEnumerable<string> items)
    {
        var added = new List<string>();
        foreach (var item in items)
        {
            var trimmed = item.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (!state.Inventory.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                state.Inventory.Add(trimmed);
                added.Add(trimmed);
            }
        }
        return added;
    }

    /// <summary>
    /// Removes items from the CYOA inventory. Case-insensitive matching.
    /// </summary>
    public static List<string> RemoveItems(CyoaState state, IEnumerable<string> items)
    {
        var removed = new List<string>();
        foreach (var item in items)
        {
            var trimmed = item.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var existing = state.Inventory.FirstOrDefault(i =>
                string.Equals(i, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                state.Inventory.Remove(existing);
                removed.Add(existing);
            }
        }
        return removed;
    }

    /// <summary>
    /// Checks whether the player has all of the specified items.
    /// </summary>
    public static bool HasItems(CyoaState state, IEnumerable<string> items)
    {
        return items.All(item =>
            state.Inventory.Contains(item.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a player-facing flavor description of the current health level.
    /// </summary>
    public static string DescribeHealth(CyoaHealthLevel health) => health switch
    {
        CyoaHealthLevel.Healthy => "You're feeling fine — ready for whatever comes next.",
        CyoaHealthLevel.Hurt => "You're battered and bruised, but still standing.",
        CyoaHealthLevel.Critical => "You're barely holding on. One more hit could be the end.",
        CyoaHealthLevel.Dead => "Everything goes dark...",
        _ => "You're not sure how you feel."
    };

    /// <summary>
    /// Interprets a narrator health-change signal and returns the new health level.
    /// Supports absolute values ("healthy", "hurt", "critical", "dead") and
    /// relative signals ("worse", "better").
    /// </summary>
    internal static CyoaHealthLevel ParseHealthChange(CyoaHealthLevel current, string change)
    {
        var normalized = change.Trim().ToLowerInvariant();
        return normalized switch
        {
            "healthy" => CyoaHealthLevel.Healthy,
            "hurt" => CyoaHealthLevel.Hurt,
            "critical" => CyoaHealthLevel.Critical,
            "dead" => CyoaHealthLevel.Dead,
            "worse" => current switch
            {
                CyoaHealthLevel.Healthy => CyoaHealthLevel.Hurt,
                CyoaHealthLevel.Hurt => CyoaHealthLevel.Critical,
                CyoaHealthLevel.Critical => CyoaHealthLevel.Dead,
                _ => current
            },
            "better" => current switch
            {
                CyoaHealthLevel.Hurt => CyoaHealthLevel.Healthy,
                CyoaHealthLevel.Critical => CyoaHealthLevel.Hurt,
                CyoaHealthLevel.Dead => CyoaHealthLevel.Critical, // resurrection is dramatic
                _ => current
            },
            _ => current
        };
    }
}
