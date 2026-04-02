namespace GAE.Core.Models;

public class Npc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Faction { get; set; } = "neutral";
    public string Disposition { get; set; } = "neutral";
    public NpcDispositionState DispositionState { get; set; } = new();
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
    /// Drifts intensity toward baseline over elapsed time.
    /// Half-life is ~1 hour: after 1 hour, half the excess intensity has faded.
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
}
