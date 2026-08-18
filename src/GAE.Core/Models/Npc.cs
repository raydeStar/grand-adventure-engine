namespace GAE.Core.Models;

public class Npc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];
    public string Faction { get; set; } = "neutral";
    public string Disposition { get; set; } = "friendly";
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
    public bool IsShopkeeper { get; set; }
    public List<InventoryItem> ShopInventory { get; set; } = [];
    public Dictionary<string, string> Dialogue { get; set; } = new();

    /// <summary>Quest definition IDs this NPC can offer to players.</summary>
    public List<string> QuestsOffered { get; set; } = [];

    /// <summary>Higher values make this NPC's quest hooks take precedence in narrator context.</summary>
    public int QuestGiverPriority { get; set; }
}

public class NpcDispositionState
{
    public string Emotion { get; set; } = "friendly";
    public int Intensity { get; set; } = 65;
    public int Baseline { get; set; } = 65;
    public string? Reason { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Permanent or long-lasting memory flags that alter decay behavior.
    /// Examples: "crime-witnessed", "romance", "friendship", "betrayal", "helped-in-battle"
    /// Flags starting with "!" are permanent (never auto-removed).
    /// </summary>
    public List<string> MemoryFlags { get; set; } = [];

    /// <summary>
    /// What this NPC remembers about this player. Serialised with the rest of the disposition state,
    /// so it persists across conversations and sessions without a schema change.
    /// </summary>
    public NpcMemoryLedger Memory { get; set; } = new();

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
        if (Emotion == "neutral" && Intensity >= 55)
            return Intensity >= 65 ? "friendly" : "neutral";

        var intensityWord = Intensity switch
        {
            >= 80 => "overwhelmingly",
            >= 65 => "very",
            >= 50 => "somewhat",
            >= 35 => "slightly",
            _ => "barely"
        };

        return $"{intensityWord} {Emotion}";
    }

    /// <summary>
    /// Folds a flat disposition word from the narrator back into this rich state.
    ///
    /// The conversation prompt asks the model for a plain word ("annoyed", "flirtatious"), and for a
    /// long time that word was written only to <see cref="Npc.Disposition"/> — an in-memory field
    /// that is never persisted per player. The rich state, which *is* persisted, therefore never
    /// moved: a player could offend an NPC, walk out, come back, and be greeted as a stranger.
    /// Mapping the word onto emotion and intensity is what makes a mood outlast the conversation.
    /// </summary>
    public void ApplyFlatDisposition(string? flat)
    {
        if (string.IsNullOrWhiteSpace(flat))
            return;

        var text = flat.Trim().ToLowerInvariant();

        // Strip any leading adverb, keeping how strongly it was meant. The adverb describes the
        // force of the emotion, not a warmth level: "overwhelmingly angry" is colder than
        // "slightly annoyed", so it has to push further from neutral rather than higher up the scale.
        double? adverbStrength = null;
        foreach (var (word, strength) in new[]
                 {
                     ("overwhelmingly", 1.0), ("deeply", 1.0), ("very", 0.75), ("quite", 0.65),
                     ("somewhat", 0.5), ("slightly", 0.25), ("mildly", 0.25), ("barely", 0.15)
                 })
        {
            if (text.StartsWith(word + " ", StringComparison.Ordinal))
            {
                adverbStrength = strength;
                text = text[(word.Length + 1)..];
                break;
            }
        }

        var emotion = text.Trim();
        if (emotion.Length == 0)
            return;

        // Where each mood sits on the 0-100 scale when the narrator gives no adverb.
        var target = emotion switch
        {
            "hostile" => 5,
            "angry" => 20,
            "contemptuous" or "disgusted" => 25,
            "annoyed" or "suspicious" => 35,
            "scared" or "sad" or "resigned" => 40,
            "neutral" => 55,
            "wary" => 45,
            "intrigued" or "amused" or "impressed" or "flustered" => 68,
            "friendly" or "grateful" => 72,
            "flirtatious" => 78,
            _ => Intensity
        };

        Emotion = emotion;

        // "somewhat" is treated as the emotion's natural strength, so a stronger adverb scales the
        // distance from neutral outward and a weaker one pulls it back toward neutral.
        const int neutralWarmth = 55;
        var desired = target;
        if (adverbStrength is { } force)
        {
            var offset = (target - neutralWarmth) * (force / 0.5);
            desired = (int)Math.Round(neutralWarmth + offset);
        }

        // Move decisively but not instantly, so one word cannot erase an established relationship.
        Intensity = Math.Clamp((int)Math.Round(Intensity * 0.35 + desired * 0.65), 0, 100);
        LastUpdated = DateTimeOffset.UtcNow;
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
