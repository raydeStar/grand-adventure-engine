using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>A registered playable race with stat bonuses and traits.</summary>
public class RaceDefinition : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>Stat bonuses applied at character creation (e.g. {"str": 2, "con": 1}).</summary>
    public Dictionary<string, int> StatBonuses { get; set; } = new();

    /// <summary>Narrative traits (e.g. "Darkvision", "Fey Ancestry").</summary>
    public List<string> Traits { get; set; } = [];

    /// <summary>Classes this race can play. Empty = all classes allowed.</summary>
    public List<string> AllowedClasses { get; set; } = [];

    /// <summary>Tags for search/filter.</summary>
    public List<string> Tags { get; set; } = [];
}
