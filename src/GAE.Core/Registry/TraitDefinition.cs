namespace GAE.Core.Registry;

/// <summary>Mechanical effect type for a racial trait.</summary>
public enum TraitEffectType
{
    /// <summary>Flavor only — no mechanical effect.</summary>
    Narrative,

    /// <summary>Reduces incoming damage of a specific type by <see cref="TraitDefinition.Value"/>.</summary>
    DamageResistance,

    /// <summary>Permanent bonus to a stat (e.g. +1 CON).</summary>
    StatBonus,

    /// <summary>Advantage on skill checks involving a specific stat.</summary>
    SkillAdvantage
}

/// <summary>A mechanical trait effect granted by a race (e.g. Armored Hide → −2 physical damage).</summary>
public class TraitDefinition
{
    /// <summary>Unique identifier (e.g. "armored_hide").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short description shown to the player.</summary>
    public string? Description { get; set; }

    /// <summary>What this trait does mechanically.</summary>
    public TraitEffectType Effect { get; set; } = TraitEffectType.Narrative;

    /// <summary>Damage type for resistance (e.g. "physical", "fire", "poison") or stat name for bonus (e.g. "con").</summary>
    public string? TargetType { get; set; }

    /// <summary>Magnitude of the effect (flat damage reduction, stat bonus, etc.).</summary>
    public int Value { get; set; }
}
