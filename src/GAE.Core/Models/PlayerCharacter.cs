namespace GAE.Core.Models;

public class PlayerCharacter
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public string? DiscordId { get; set; }
    public ulong? ThreadId { get; set; }
    public bool HasCompletedDemo { get; set; }
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

    /// <summary>Returns attribute name/value pairs for display.</summary>
    public IEnumerable<(string Name, int Value)> GetAttributeStats()
    {
        yield return ("STR", Str);
        yield return ("DEX", Dex);
        yield return ("CON", Con);
        yield return ("INT", Int);
        yield return ("WIS", Wis);
        yield return ("CHA", Cha);
        yield return ("LUCK", Luck);
    }

    /// <summary>Formats stats as a compact display string for prompts. Example: STR:12 DEX:10 ...</summary>
    public string FormatStatsCompact() =>
        string.Join(" ", GetAttributeStats().Select(s => $"{s.Name}:{s.Value}"));

    /// <summary>Formats stats with modifiers for detailed display. Example: STR: 12 (+1) | DEX: 10 (+0)</summary>
    public string FormatStatsDetailed(string separator = " | ") =>
        string.Join(separator, GetAttributeStats().Select(s =>
            $"{s.Name}: {s.Value} ({GetStatModifier(s.Value):+0;-0})"));

    public bool IsAlive => Hp > 0;
    public bool IsConscious => Hp > 0;

    public int Defense => 10
        + GetStatModifier(Dex)
        + (Equipment.Armor?.ArmorValue ?? 0)
        + (Equipment.Shield?.ArmorValue ?? 0)
        + (Equipment.Helmet?.ArmorValue ?? 0);
}
