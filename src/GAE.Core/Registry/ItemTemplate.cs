using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// A registered item template. Items created in-game should reference a template
/// for their stats. Unregistered items created by the LLM are flagged as improvised.
/// </summary>
public class ItemTemplate : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    public ItemType Type { get; set; } = ItemType.Misc;
    public string? DamageDice { get; set; }
    public string? DamageStat { get; set; }
    public int ArmorValue { get; set; }
    public bool IsEquippable { get; set; }
    public bool IsConsumable { get; set; }
    public bool IsTwoHanded { get; set; }
    public string? Effect { get; set; }
    public int Value { get; set; }

    /// <summary>Stat bonuses when equipped (e.g. "cha": 2).</summary>
    public Dictionary<string, int> StatBonuses { get; set; } = new();

    /// <summary>Rarity tier: common, uncommon, rare, epic, legendary.</summary>
    public string Rarity { get; set; } = "common";

    /// <summary>Classes that can use this item. Empty = all.</summary>
    public List<string> RequiredClasses { get; set; } = [];

    /// <summary>Minimum level to equip/use.</summary>
    public int RequiredLevel { get; set; } = 1;

    /// <summary>Tags for search/filter.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Create an InventoryItem instance from this template.</summary>
    public InventoryItem ToInventoryItem(int quantity = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        Description = Description ?? string.Empty,
        Type = Type,
        Quantity = quantity,
        Value = Value,
        DamageDice = DamageDice,
        DamageStat = DamageStat,
        ArmorValue = ArmorValue,
        IsEquippable = IsEquippable,
        IsConsumable = IsConsumable,
        IsTwoHanded = IsTwoHanded,
        Effect = Effect,
        StatBonuses = new Dictionary<string, int>(StatBonuses)
    };
}
