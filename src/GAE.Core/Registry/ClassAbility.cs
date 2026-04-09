namespace GAE.Core.Registry;

/// <summary>What an ability does when used.</summary>
public enum AbilityEffectType
{
    /// <summary>Deals damage to a target (uses <see cref="ClassAbility.DamageDice"/>).</summary>
    Damage,

    /// <summary>Heals the user (uses <see cref="ClassAbility.HealAmount"/>).</summary>
    Heal,

    /// <summary>Applies a temporary stat buff to the user.</summary>
    Buff,

    /// <summary>Applies a status effect to a target.</summary>
    StatusEffect
}

/// <summary>An active ability granted by a class at a specific level.</summary>
public class ClassAbility
{
    /// <summary>Unique identifier (e.g. "shield_bash").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short description shown to the player.</summary>
    public string? Description { get; set; }

    /// <summary>Minimum player level to unlock this ability.</summary>
    public int UnlockLevel { get; set; } = 1;

    /// <summary>MP cost to use. 0 = free.</summary>
    public int MpCost { get; set; }

    /// <summary>Cooldown in combat turns after use. 0 = no cooldown.</summary>
    public int CooldownTurns { get; set; }

    /// <summary>What happens when this ability is used.</summary>
    public AbilityEffectType Effect { get; set; } = AbilityEffectType.Damage;

    /// <summary>Damage dice for Damage abilities (e.g. "2d6").</summary>
    public string? DamageDice { get; set; }

    /// <summary>Flat heal amount for Heal abilities.</summary>
    public int? HealAmount { get; set; }

    /// <summary>Stat affected by Buff abilities (e.g. "str").</summary>
    public string? TargetStat { get; set; }

    /// <summary>Buff magnitude or status effect power.</summary>
    public int? BuffValue { get; set; }

    /// <summary>Duration in turns for Buff / StatusEffect abilities.</summary>
    public int? Duration { get; set; }
}
