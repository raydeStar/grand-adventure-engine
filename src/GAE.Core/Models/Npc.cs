namespace GAE.Core.Models;

public class Npc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Faction { get; set; } = "neutral";
    public string Disposition { get; set; } = "neutral";
    public bool IsHostile { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? AttackBonus { get; set; }
    public string? DamageDice { get; set; }
    public int? Defense { get; set; }
    public int Level { get; set; } = 1;
    public List<InventoryItem> LootTable { get; set; } = [];
    public Dictionary<string, string> Dialogue { get; set; } = new();
}
