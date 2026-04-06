using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>A registered playable class with stat priorities, allowed spells, and equipment restrictions.</summary>
public class ClassDefinition : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>Hit die per level (e.g. "d10", "d6").</summary>
    public string HitDie { get; set; } = "d8";

    /// <summary>Primary casting/combat stat (str, dex, int, wis, cha).</summary>
    public string PrimaryStat { get; set; } = "str";

    /// <summary>Secondary stat for the class.</summary>
    public string? SecondaryStat { get; set; }

    /// <summary>Base MP bonus at level 1.</summary>
    public int BaseMpBonus { get; set; }

    /// <summary>Whether this class can use magic at all.</summary>
    public bool CanCastSpells { get; set; }

    /// <summary>Spell IDs this class can learn. Empty + CanCastSpells = learns from scrolls only.</summary>
    public List<string> SpellList { get; set; } = [];

    /// <summary>Allowed weapon types (Weapon = all weapons, or specific sub-types).</summary>
    public List<string> AllowedWeaponTypes { get; set; } = ["Weapon"];

    /// <summary>Allowed armor types (Armor, Shield, Helmet, or "light", "heavy").</summary>
    public List<string> AllowedArmorTypes { get; set; } = ["Armor", "Shield", "Helmet"];

    /// <summary>Stat bonuses applied at character creation.</summary>
    public Dictionary<string, int> StatBonuses { get; set; } = new();

    /// <summary>Starting equipment item IDs.</summary>
    public List<string> StartingEquipment { get; set; } = [];

    /// <summary>Maximum improvised spell power level at each character level. Index 0 = level 1.</summary>
    public List<int> ImprovisedSpellCap { get; set; } = [2, 3, 4, 5, 6, 7, 8, 9, 10, 10];

    /// <summary>Tags for search/filter.</summary>
    public List<string> Tags { get; set; } = [];
}
