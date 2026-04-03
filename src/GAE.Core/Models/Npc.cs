namespace GAE.Core.Models;

public class Npc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Faction { get; set; } = "neutral";
    public string Disposition { get; set; } = "neutral";
    public NpcDispositionState DispositionState { get; set; } = new();
    public List<string> KnowledgeScopes { get; set; } = [];
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

public class NpcDispositionState
{
    public string Emotion { get; set; } = "neutral";
    public int Intensity { get; set; } = 40;
    public int Baseline { get; set; } = 40;
    public string? Reason { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Permanent or long-lasting memory flags that alter decay behavior.
    /// Examples: "crime-witnessed", "romance", "friendship", "betrayal", "helped-in-battle"
    /// Flags starting with "!" are permanent (never auto-removed).
    /// </summary>
    public List<string> MemoryFlags { get; set; } = [];

    /// <summary>
    /// Drifts intensity toward baseline over elapsed time.
    /// Half-life is ~1 hour: after 1 hour, half the excess intensity has faded.
    /// Memory flags can lock a minimum/maximum intensity floor.
    /// </summary>
    public void DecayTowardBaseline(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;

        var excess = Intensity - Baseline;
        if (Math.Abs(excess) < 1) return;

        // Exponential decay with ~1 hour half-life
        var halfLifeHours = 1.0;
        var decayFactor = Math.Pow(0.5, elapsed.TotalHours / halfLifeHours);
        Intensity = Baseline + (int)Math.Round(excess * decayFactor);
        LastUpdated = DateTimeOffset.UtcNow;

        // Memory flags can enforce intensity floors/ceilings
        var floor = GetMemoryFloor();
        var ceiling = GetMemoryCeiling();
        Intensity = Math.Clamp(Intensity, floor, ceiling);

        // If decayed close to baseline and emotion was transient, reset to neutral
        if (Math.Abs(Intensity - Baseline) <= 5 && Emotion != "neutral")
        {
            Emotion = "neutral";
            Reason = null;
        }
    }

    /// <summary>Flat string summary for the Disposition field sync.</summary>
    public string ToFlatDisposition()
    {
        if (Emotion == "neutral" || Intensity <= Baseline)
            return "neutral";

        var intensityWord = Intensity switch
        {
            >= 80 => "overwhelmingly",
            >= 60 => "very",
            >= 45 => "somewhat",
            _ => "slightly"
        };

        return $"{intensityWord} {Emotion}";
    }

    /// <summary>
    /// Positive memory flags enforce a minimum intensity (NPC can't fully forget good things).
    /// </summary>
    private int GetMemoryFloor()
    {
        if (MemoryFlags.Any(f => f.Contains("romance", StringComparison.OrdinalIgnoreCase)))
            return 65; // Romance prevents falling below "very" friendly
        if (MemoryFlags.Any(f => f.Contains("friendship", StringComparison.OrdinalIgnoreCase)))
            return 50; // Friendship prevents falling below "somewhat" friendly
        return 0;
    }

    /// <summary>
    /// Negative memory flags enforce a maximum intensity cap (NPC remembers wrongs).
    /// Crime/betrayal keep hostility from fading too much.
    /// </summary>
    private int GetMemoryCeiling()
    {
        if (MemoryFlags.Any(f => f.Contains("betrayal", StringComparison.OrdinalIgnoreCase)))
            return 25; // Betrayal keeps intensity low (angry)
        if (MemoryFlags.Any(f => f.Contains("crime", StringComparison.OrdinalIgnoreCase)))
            return 35; // Crime keeps intensity suppressed
        return 100;
    }
}
