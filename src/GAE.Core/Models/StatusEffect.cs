namespace GAE.Core.Models;

public class StatusEffect
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public StatusEffectType Type { get; set; }
    public int RemainingTurns { get; set; }
    public Dictionary<string, int> StatModifiers { get; set; } = new();
    public int? DamagePerTurn { get; set; }
    public int? HealPerTurn { get; set; }
}

public enum StatusEffectType
{
    Buff,
    Debuff,
    Poison,
    Regen,
    Stun,
    Blind,
    Charm
}
