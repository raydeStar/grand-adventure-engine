namespace GAE.Core.Models;

public class LearnedSpell
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DamageDice { get; set; } = string.Empty;  // level-scaled
    public string DamageStat { get; set; } = "int";
    public SpellCategory Category { get; set; }
    public int MpCost { get; set; }
    public int BasePower { get; set; }  // AI-assigned 1-10
    public int LearnedAtLevel { get; set; }
    public string? TargetType { get; set; }  // "enemy", "self", "area"
}

public enum SpellCategory
{
    Damage,
    Healing,
    Buff,
    Debuff,
    Utility
}
