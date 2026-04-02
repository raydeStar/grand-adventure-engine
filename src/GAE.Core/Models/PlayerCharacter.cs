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

    // Data-driven attributes — source of truth
    public Dictionary<string, int> Stats { get; set; } = new()
    {
        ["str"] = 10, ["dex"] = 10, ["con"] = 10,
        ["int"] = 10, ["wis"] = 10, ["cha"] = 10, ["luck"] = 10
    };

    // Backward-compat computed properties — delegate to Stats dictionary
    public int Str { get => Stats.GetValueOrDefault("str", 10); set => Stats["str"] = value; }
    public int Dex { get => Stats.GetValueOrDefault("dex", 10); set => Stats["dex"] = value; }
    public int Con { get => Stats.GetValueOrDefault("con", 10); set => Stats["con"] = value; }
    public int Int { get => Stats.GetValueOrDefault("int", 10); set => Stats["int"] = value; }
    public int Wis { get => Stats.GetValueOrDefault("wis", 10); set => Stats["wis"] = value; }
    public int Cha { get => Stats.GetValueOrDefault("cha", 10); set => Stats["cha"] = value; }
    public int Luck { get => Stats.GetValueOrDefault("luck", 10); set => Stats["luck"] = value; }

    // Interaction state
    public InteractionState Interaction { get; set; } = new();

    // Equipment and inventory
    public EquipmentLoadout Equipment { get; set; } = new();
    public List<InventoryItem> Inventory { get; set; } = [];
    public List<StatusEffect> StatusEffects { get; set; } = [];

    // Timestamps
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;

    public static int GetStatModifier(int statValue) => (statValue - 10) / 2;

    public int GetModifier(string stat) =>
        Stats.TryGetValue(stat.ToLowerInvariant(), out var value) ? GetStatModifier(value) : 0;

    /// <summary>Returns attribute stats (non-resource) for dynamic display.</summary>
    public IEnumerable<KeyValuePair<string, int>> GetAttributeStats() =>
        Stats.Where(kv => kv.Key is not ("hp" or "mp"));

    /// <summary>Formats stats as a compact display string for prompts.</summary>
    public string FormatStatsCompact() =>
        string.Join(" ", GetAttributeStats().Select(kv => $"{kv.Key.ToUpperInvariant()}:{kv.Value}"));

    /// <summary>Formats stats with modifiers for detailed display.</summary>
    public string FormatStatsDetailed(string separator = " | ") =>
        string.Join(separator, GetAttributeStats().Select(kv =>
            $"{kv.Key.ToUpperInvariant()}: {kv.Value} ({GetStatModifier(kv.Value):+0;-0})"));

    public bool IsAlive => Hp > 0;
    public bool IsConscious => Hp > 0;

    public int Defense => 10 + GetModifier("dex") + (Equipment.Armor?.ArmorValue ?? 0);
}
