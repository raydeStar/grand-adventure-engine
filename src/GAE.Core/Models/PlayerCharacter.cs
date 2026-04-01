namespace GAE.Core.Models;

public class PlayerCharacter
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string CurrentRoomId { get; set; } = "spawn";

    // Resources
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mp { get; set; }
    public int MaxMp { get; set; }
    public int Gold { get; set; }
    public int Xp { get; set; }
    public int Level { get; set; } = 1;

    // Attributes
    public int Str { get; set; } = 10;
    public int Dex { get; set; } = 10;
    public int Con { get; set; } = 10;
    public int Int { get; set; } = 10;
    public int Wis { get; set; } = 10;
    public int Cha { get; set; } = 10;
    public int Luck { get; set; } = 10;

    // Equipment and inventory
    public EquipmentLoadout Equipment { get; set; } = new();
    public List<InventoryItem> Inventory { get; set; } = [];
    public List<StatusEffect> StatusEffects { get; set; } = [];

    // Timestamps
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;

    public int GetStatModifier(int statValue) => (statValue - 10) / 2;

    public int GetModifier(string stat) => stat.ToLowerInvariant() switch
    {
        "str" => GetStatModifier(Str),
        "dex" => GetStatModifier(Dex),
        "con" => GetStatModifier(Con),
        "int" => GetStatModifier(Int),
        "wis" => GetStatModifier(Wis),
        "cha" => GetStatModifier(Cha),
        "luck" => GetStatModifier(Luck),
        _ => 0
    };

    public bool IsAlive => Hp > 0;
    public bool IsConscious => Hp > 0;

    public int Defense => 10 + GetStatModifier(Dex) + (Equipment.Armor?.ArmorValue ?? 0);
}
