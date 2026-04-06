using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// A registered spell definition. Spells in the registry work mechanically with
/// guaranteed stats. Improvised spells (not in registry) go through power-budget evaluation.
/// </summary>
public class SpellDefinition : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>Spell school (evocation, restoration, illusion, conjuration, necromancy, abjuration, divination, transmutation).</summary>
    public string School { get; set; } = "evocation";

    /// <summary>Mana cost to cast.</summary>
    public int ManaCost { get; set; }

    /// <summary>Damage dice expression (e.g. "2d6+3"). Null for non-damage spells.</summary>
    public string? DamageDice { get; set; }

    /// <summary>Stat used for damage modifier (int, wis, cha).</summary>
    public string? DamageStat { get; set; }

    /// <summary>Healing dice expression (e.g. "1d8+2"). Null for non-healing spells.</summary>
    public string? HealDice { get; set; }

    /// <summary>Status effect applied by this spell (references StatusEffectType).</summary>
    public string? StatusEffect { get; set; }

    /// <summary>Duration in turns for status effects. 0 = instant.</summary>
    public int Duration { get; set; }

    /// <summary>Range: self, touch, ranged.</summary>
    public string Range { get; set; } = "ranged";

    /// <summary>Classes that can learn this spell. Empty = all classes.</summary>
    public List<string> RequiredClasses { get; set; } = [];

    /// <summary>Minimum character level required.</summary>
    public int RequiredLevel { get; set; } = 1;

    /// <summary>Power level 1-10, used for balancing and improvised spell comparison.</summary>
    public int PowerLevel { get; set; } = 1;

    /// <summary>Tags for search/filter (e.g. "fire", "aoe", "single-target").</summary>
    public List<string> Tags { get; set; } = [];
}
