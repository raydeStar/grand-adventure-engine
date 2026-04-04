using System.Text.Json.Serialization;

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

    /// <summary>
    /// The item's level, used for level-gating. Players can only acquire items
    /// up to (their level + 1). Items without an explicit level default to 1.
    /// </summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// Stat bonuses granted while this item is equipped.
    /// Keys are lowercase stat names (str, dex, con, int, wis, cha, luck).
    /// Values are the flat bonus (e.g. +2).
    /// </summary>
    public Dictionary<string, int> StatBonuses { get; set; } = new();

    /// <summary>
    /// True for greatswords, staves, bows — occupies both hands.
    /// </summary>
    public bool IsTwoHanded { get; set; }

    /// <summary>Returns true if this item type is inherently equippable (gear, not consumables/quest items).</summary>
    public static bool IsEquippableType(ItemType type) => type is
        ItemType.Weapon or ItemType.Armor or ItemType.Shield or ItemType.Helmet
        or ItemType.Cloak or ItemType.Boots or ItemType.Gloves
        or ItemType.Ring or ItemType.Amulet or ItemType.Bracelet;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemType
{
    Weapon,
    Armor,
    Shield,
    Helmet,
    Cloak,
    Boots,
    Gloves,
    Ring,
    Amulet,
    Bracelet,
    Potion,
    Scroll,
    Key,
    QuestItem,
    Misc
}
