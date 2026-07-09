using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// Centralizes semantic item-template choices that must stay consistent across engine and dashboard flows.
/// </summary>
public static class ItemTemplateSelection
{
    /// <summary>
    /// Resolves the lowest-level, lowest-value healing potion when authored content asks for a generic potion.
    /// </summary>
    public static ItemTemplate? ResolveGenericHealingPotion(IEnumerable<ItemTemplate> templates)
    {
        return templates
            .Where(candidate => candidate.IsConsumable)
            .Where(candidate =>
                candidate.Type == ItemType.Potion
                || candidate.Tags.Contains("potion", StringComparer.OrdinalIgnoreCase)
                || candidate.Tags.Contains("healing", StringComparer.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.RequiredLevel)
            .ThenBy(candidate => candidate.Value)
            .FirstOrDefault();
    }
}
