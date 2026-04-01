namespace GAE.Core.Models;

public class InventoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ItemType Type { get; set; }
    public int Quantity { get; set; } = 1;
    public int Value { get; set; }
    public string? DamageDice { get; set; }
    public string? DamageStat { get; set; }
    public int ArmorValue { get; set; }
    public bool IsEquippable { get; set; }
    public bool IsConsumable { get; set; }
    public string? Effect { get; set; }
}

public enum ItemType
{
    Weapon,
    Armor,
    Shield,
    Helmet,
    Potion,
    Scroll,
    Key,
    QuestItem,
    Misc
}
