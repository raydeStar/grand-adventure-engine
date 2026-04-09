namespace GAE.Core.Models;

public class PlayerCharacter
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Faction { get; set; } = "neutral";
    public string ActiveWorldId { get; set; } = WorldDefaults.DefaultWorldId;
    public string HomeWorldId { get; set; } = WorldDefaults.DefaultWorldId;
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

    // Game mode
    /// <summary>Which rule set this player uses. Defaults to FullRpg.</summary>
    public GameMode GameMode { get; set; } = GameMode.FullRpg;

    // Interaction state
    public InteractionState Interaction { get; set; } = new();

    /// <summary>Active Blind Adventure session, or null if not in one.</summary>
    public BlindAdventureSession? BlindAdventure { get; set; }

    /// <summary>Active CYOA session state, or null if not in one.</summary>
    public CyoaState? CyoaState { get; set; }

    // Equipment and inventory
    public EquipmentLoadout Equipment { get; set; } = new();
    public List<InventoryItem> Inventory { get; set; } = [];
    public List<StatusEffect> StatusEffects { get; set; } = [];
    public List<LearnedSpell> Spellbook { get; set; } = [];

    // Quest log
    public List<QuestProgress> QuestLog { get; set; } = [];

    /// <summary>Messages queued for delivery on the player's next action (e.g. party quest updates while offline).</summary>
    public List<string> PendingNotifications { get; set; } = [];

    // Lore & narrator
    /// <summary>IDs of lore entries this player has discovered.</summary>
    public List<string> DiscoveredLore { get; set; } = [];

    /// <summary>ID of the narrator preset assigned to this player. Null = world default.</summary>
    public string? NarratorPresetId { get; set; }

    // Timestamps
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Calculates the modifier for a raw stat value using the given baseline.
    /// Formula: (statValue - baseline) / 2. A stat equal to the baseline yields +0.
    /// </summary>
    public static int GetStatModifier(int statValue, int baseline = 10) => (statValue - baseline) / 2;

    /// <summary>
    /// Returns the total modifier for a stat: base stat modifier + equipment bonuses.
    /// </summary>
    public int GetModifier(string stat, int baseline = 10)
    {
        int baseMod = stat.ToLowerInvariant() switch
        {
            "str" => GetStatModifier(Str, baseline),
            "dex" => GetStatModifier(Dex, baseline),
            "con" => GetStatModifier(Con, baseline),
            "int" => GetStatModifier(Int, baseline),
            "wis" => GetStatModifier(Wis, baseline),
            "cha" => GetStatModifier(Cha, baseline),
            "luck" => GetStatModifier(Luck, baseline),
            _ => 0
        };
        return baseMod + Equipment.GetStatBonus(stat);
    }

    /// <summary>Returns the raw base modifier without equipment bonuses.</summary>
    public int GetBaseModifier(string stat, int baseline = 10) => stat.ToLowerInvariant() switch
    {
        "str" => GetStatModifier(Str, baseline),
        "dex" => GetStatModifier(Dex, baseline),
        "con" => GetStatModifier(Con, baseline),
        "int" => GetStatModifier(Int, baseline),
        "wis" => GetStatModifier(Wis, baseline),
        "cha" => GetStatModifier(Cha, baseline),
        "luck" => GetStatModifier(Luck, baseline),
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

    /// <summary>
    /// Formats stats with modifiers for detailed display. Example: STR: 12 (+1) | DEX: 10 (+0).
    /// If baseline is null, modifiers are omitted: STR: 12 | DEX: 10.
    /// </summary>
    public string FormatStatsDetailed(string separator = " | ", int? baseline = 10) =>
        baseline is int b
            ? string.Join(separator, GetAttributeStats().Select(s =>
                $"{s.Name}: {s.Value} ({GetStatModifier(s.Value, b):+0;-0})"))
            : string.Join(separator, GetAttributeStats().Select(s =>
                $"{s.Name}: {s.Value}"));

    public bool IsAlive => Hp > 0;
    public bool IsConscious => Hp > 0;

    public int Defense => 10
        + GetModifier("dex")
        + Equipment.TotalArmorValue();
}
