namespace GAE.Core.Registry;

/// <summary>Result of LLM evaluation of an improvised (unregistered) spell attempt.</summary>
public class ImprovisedSpellResult
{
    /// <summary>Power level 1-10 the LLM assessed for this spell.</summary>
    public int PowerLevel { get; set; }

    /// <summary>Maximum power level the character can handle.</summary>
    public int PlayerCap { get; set; }

    /// <summary>Whether the spell succeeds (power <= cap).</summary>
    public bool Success { get; set; }

    /// <summary>Mana cost assessed by the LLM.</summary>
    public int ManaCost { get; set; }

    /// <summary>Damage dealt (0 if non-damage or fizzle).</summary>
    public int Damage { get; set; }

    /// <summary>Healing done (0 if non-healing or fizzle).</summary>
    public int Healing { get; set; }

    /// <summary>Narrative description of what happened.</summary>
    public string Narration { get; set; } = string.Empty;

    /// <summary>Stat changes to apply (e.g. hp, mp, gold).</summary>
    public Dictionary<string, int> StatChanges { get; set; } = new();

    /// <summary>Target that was affected, if any.</summary>
    public string? Target { get; set; }
}
