using System.Text.Json.Serialization;

namespace GAE.Core.Models;

public class EquipmentLoadout
{
    // --- Hand slots (2 hands) ---
    public InventoryItem? MainHand { get; set; }
    public InventoryItem? OffHand { get; set; }

    // --- Single-slot gear ---
    public InventoryItem? Armor { get; set; }
    public InventoryItem? Helmet { get; set; }
    public InventoryItem? Cloak { get; set; }
    public InventoryItem? Boots { get; set; }
    public InventoryItem? Gloves { get; set; }

    // --- Stackable accessories (wear as many as you want) ---
    public List<InventoryItem> Rings { get; set; } = [];
    public List<InventoryItem> Amulets { get; set; } = [];
    public List<InventoryItem> Bracelets { get; set; } = [];

    // ── Backward-compatible aliases ──
    // Old save data has "weapon" and "shield" as top-level fields.
    // These setters migrate old data into MainHand/OffHand on deserialization.
    // Getters provide convenient access.

    /// <summary>Primary weapon in main hand. Setter maps to MainHand for backward compat.</summary>
    public InventoryItem? Weapon
    {
        get => MainHand?.Type == ItemType.Weapon ? MainHand : null;
        set { if (value is not null) MainHand ??= value; }
    }

    /// <summary>Shield in off-hand. Setter maps to OffHand for backward compat.</summary>
    public InventoryItem? Shield
    {
        get => OffHand?.Type == ItemType.Shield ? OffHand : null;
        set { if (value is not null) OffHand ??= value; }
    }

    /// <summary>Returns every currently equipped item (all slots + all accessories).</summary>
    public IEnumerable<InventoryItem> AllEquipped()
    {
        if (MainHand is not null) yield return MainHand;
        if (OffHand is not null) yield return OffHand;
        if (Armor is not null) yield return Armor;
        if (Helmet is not null) yield return Helmet;
        if (Cloak is not null) yield return Cloak;
        if (Boots is not null) yield return Boots;
        if (Gloves is not null) yield return Gloves;
        foreach (var r in Rings) yield return r;
        foreach (var a in Amulets) yield return a;
        foreach (var b in Bracelets) yield return b;
    }

    /// <summary>Total stat bonus from all equipped items for a given stat (e.g. "cha").</summary>
    public int GetStatBonus(string stat)
    {
        var key = stat.ToLowerInvariant();
        int total = 0;
        foreach (var item in AllEquipped())
        {
            if (item.StatBonuses.TryGetValue(key, out var bonus))
                total += bonus;
        }
        return total;
    }

    /// <summary>Total armor value from all equipped items.</summary>
    public int TotalArmorValue()
    {
        int total = 0;
        foreach (var item in AllEquipped())
            total += item.ArmorValue;
        return total;
    }

    /// <summary>
    /// Tries to equip an item into the correct slot. Returns the slot name on success, null if not equippable.
    /// Any displaced item is returned via <paramref name="displaced"/>.
    /// </summary>
    public string? Equip(InventoryItem item, out List<InventoryItem> displaced)
    {
        displaced = [];

        switch (item.Type)
        {
            case ItemType.Weapon:
                if (item.IsTwoHanded)
                {
                    // Two-handed: clear both hands
                    if (MainHand is not null) displaced.Add(MainHand);
                    if (OffHand is not null) displaced.Add(OffHand);
                    MainHand = item;
                    OffHand = null;
                }
                else
                {
                    // One-handed: always goes to main hand — the new weapon is what the player wants to wield
                    if (MainHand is null)
                    {
                        MainHand = item;
                    }
                    else
                    {
                        // Displace current main hand (whether one-handed or two-handed)
                        displaced.Add(MainHand);
                        MainHand = item;
                        // If old weapon was two-handed, off-hand is already null; nothing extra to do
                    }
                }
                return "Weapon";

            case ItemType.Shield:
                if (MainHand?.IsTwoHanded == true)
                {
                    // Can't use shield with two-handed weapon — drop the two-hander
                    displaced.Add(MainHand);
                    MainHand = null;
                }
                if (OffHand is not null) displaced.Add(OffHand);
                OffHand = item;
                return "Shield";

            case ItemType.Armor:
                if (Armor is not null) displaced.Add(Armor);
                Armor = item;
                return "Armor";

            case ItemType.Helmet:
                if (Helmet is not null) displaced.Add(Helmet);
                Helmet = item;
                return "Helmet";

            case ItemType.Cloak:
                if (Cloak is not null) displaced.Add(Cloak);
                Cloak = item;
                return "Cloak";

            case ItemType.Boots:
                if (Boots is not null) displaced.Add(Boots);
                Boots = item;
                return "Boots";

            case ItemType.Gloves:
                if (Gloves is not null) displaced.Add(Gloves);
                Gloves = item;
                return "Gloves";

            // Stackable — unlimited
            case ItemType.Ring:
                Rings.Add(item);
                return "Ring";

            case ItemType.Amulet:
                Amulets.Add(item);
                return "Amulet";

            case ItemType.Bracelet:
                Bracelets.Add(item);
                return "Bracelet";

            default:
                return null;
        }
    }

    /// <summary>
    /// Removes a specific item from equipped slots. Returns true if found and removed.
    /// </summary>
    public bool Unequip(InventoryItem item)
    {
        if (ReferenceEquals(MainHand, item)) { MainHand = null; return true; }
        if (ReferenceEquals(OffHand, item)) { OffHand = null; return true; }
        if (ReferenceEquals(Armor, item)) { Armor = null; return true; }
        if (ReferenceEquals(Helmet, item)) { Helmet = null; return true; }
        if (ReferenceEquals(Cloak, item)) { Cloak = null; return true; }
        if (ReferenceEquals(Boots, item)) { Boots = null; return true; }
        if (ReferenceEquals(Gloves, item)) { Gloves = null; return true; }

        if (Rings.Remove(item)) return true;
        if (Amulets.Remove(item)) return true;
        if (Bracelets.Remove(item)) return true;

        return false;
    }
}
