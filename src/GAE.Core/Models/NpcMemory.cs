namespace GAE.Core.Models;

/// <summary>
/// What one NPC remembers about one player, in one world.
///
/// This lives inside <see cref="NpcDispositionState"/> so it rides the existing
/// <c>world_npc_states.disposition_state</c> jsonb column — already keyed by (npc, world, player) —
/// which means no schema migration and rows written before this existed simply deserialise with an
/// empty ledger.
///
/// The ledger is deliberately small. Everything here is rendered into narrator prompts, and the
/// narrator runs on a local model with a finite context window, so memory is stored as a handful of
/// short lines rather than transcripts. Raw history already lives in <c>conversation_logs</c>; this
/// is the part the NPC actually carries around.
/// </summary>
public class NpcMemoryLedger
{
    /// <summary>How many separate conversations this player has had with this NPC.</summary>
    public int EncounterCount { get; set; }

    /// <summary>When they last spoke. Drives forgetting.</summary>
    public DateTimeOffset? LastSpokeAt { get; set; }

    /// <summary>Distilled recollections, newest last.</summary>
    public List<NpcMemoryEntry> Entries { get; set; } = [];

    /// <summary>
    /// Subjects already covered, so an NPC can say "I already told you about the mine" instead of
    /// repeating themselves.
    /// </summary>
    public List<string> TopicsDiscussed { get; set; } = [];

    /// <summary>
    /// Things the NPC offered and has not delivered. Pete promising a story for a drink and then
    /// never telling it is the failure this exists to prevent.
    /// </summary>
    public List<NpcPromise> OpenPromises { get; set; } = [];

    /// <summary>Most direct recollections kept. Older, weaker ones are forgotten first.</summary>
    public const int MaxDirectEntries = 8;

    /// <summary>Hearsay is capped lower than first-hand memory — it matters less and blurs faster.</summary>
    public const int MaxGossipEntries = 3;

    /// <summary>Below this weight a non-core memory is dropped entirely.</summary>
    public const int ForgettingThreshold = 12;

    /// <summary>Subjects tracked before the oldest are dropped.</summary>
    public const int MaxTopics = 12;

    /// <summary>
    /// Records a recollection, replacing a near-duplicate rather than stacking repeats, then trims
    /// the ledger back to its caps.
    /// </summary>
    public void Remember(NpcMemoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Summary))
            return;

        entry.Summary = entry.Summary.Trim();

        // A repeated event should strengthen the existing memory, not add a second copy of it.
        var existing = Entries.FirstOrDefault(e =>
            e.Source == entry.Source
            && string.Equals(e.Summary, entry.Summary, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Weight = Math.Min(100, Math.Max(existing.Weight, entry.Weight) + 5);
            existing.IsCore |= entry.IsCore;
            existing.RecordedAt = entry.RecordedAt;
            return;
        }

        Entries.Add(entry);
        TrimToCaps();
    }

    /// <summary>Notes a subject as covered.</summary>
    public void NoteTopic(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return;

        var normalized = topic.Trim().ToLowerInvariant();
        if (TopicsDiscussed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return;

        TopicsDiscussed.Add(normalized);
        while (TopicsDiscussed.Count > MaxTopics)
            TopicsDiscussed.RemoveAt(0);
    }

    /// <summary>Records something the NPC owes the player.</summary>
    public void PromiseSomething(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return;

        var trimmed = summary.Trim();
        if (OpenPromises.Any(p => string.Equals(p.Summary, trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        OpenPromises.Add(new NpcPromise { Summary = trimmed, MadeAt = DateTimeOffset.UtcNow });
        while (OpenPromises.Count > 4)
            OpenPromises.RemoveAt(0);
    }

    /// <summary>Marks a promise settled so the NPC stops bringing it up.</summary>
    public void SettlePromise(string summaryFragment)
    {
        if (string.IsNullOrWhiteSpace(summaryFragment)) return;

        OpenPromises.RemoveAll(p =>
            p.Summary.Contains(summaryFragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fades memory with elapsed time. Ordinary recollections lose weight and eventually drop out;
    /// core ones — love, betrayal, a crime witnessed — never fade away, which is the difference
    /// between an NPC forgetting a passing rudeness and forgetting that you robbed them.
    /// </summary>
    public void Forget(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || Entries.Count == 0)
            return;

        // Roughly a one-week half-life: a week later an ordinary memory is half as vivid.
        var decay = Math.Pow(0.5, elapsed.TotalDays / 7.0);

        foreach (var entry in Entries.Where(e => !e.IsCore))
            entry.Weight = (int)Math.Round(entry.Weight * decay);

        Entries.RemoveAll(e => !e.IsCore && e.Weight < ForgettingThreshold);
    }

    /// <summary>
    /// Renders the ledger as a compact prompt block, or null when there is nothing to say. Kept
    /// short on purpose: the strongest memories only, newest first.
    /// </summary>
    public string? BuildPromptBlock(string playerName)
    {
        var hasAnything = Entries.Count > 0 || OpenPromises.Count > 0 || TopicsDiscussed.Count > 0 || EncounterCount > 0;
        if (!hasAnything)
            return null;

        var lines = new List<string>();

        if (EncounterCount > 0)
        {
            var when = LastSpokeAt is null ? "" : $", last spoke {DescribeElapsed(DateTimeOffset.UtcNow - LastSpokeAt.Value)}";
            lines.Add(EncounterCount == 1
                ? $"You have met {playerName} once before{when}."
                : $"You have spoken with {playerName} {EncounterCount} times before{when}.");
        }

        var strongest = Entries
            .OrderByDescending(e => e.IsCore)
            .ThenByDescending(e => e.Weight)
            .ThenByDescending(e => e.RecordedAt)
            .Take(5)
            .ToList();

        if (strongest.Count > 0)
        {
            lines.Add("What you remember:");
            foreach (var entry in strongest)
            {
                var hearsay = entry.Source == NpcMemorySource.Gossip ? " (heard second-hand, details hazy)" : "";
                lines.Add($"  - {entry.Summary}{hearsay}");
            }
        }

        if (OpenPromises.Count > 0)
        {
            lines.Add("You still owe them:");
            foreach (var promise in OpenPromises)
                lines.Add($"  - {promise.Summary}");
            lines.Add("  Deliver on this when they ask. Do not restate the offer as if it were new.");
        }

        if (TopicsDiscussed.Count > 0)
            lines.Add($"Already discussed (do not repeat as news): {string.Join(", ", TopicsDiscussed.TakeLast(6))}");

        return string.Join("\n", lines);
    }

    private void TrimToCaps()
    {
        TrimSource(NpcMemorySource.Direct, MaxDirectEntries);
        TrimSource(NpcMemorySource.Gossip, MaxGossipEntries);
    }

    private void TrimSource(NpcMemorySource source, int cap)
    {
        var ofSource = Entries.Where(e => e.Source == source).ToList();
        if (ofSource.Count <= cap)
            return;

        // Drop the weakest first, and never drop a core memory to make room.
        foreach (var doomed in ofSource
                     .OrderBy(e => e.IsCore)
                     .ThenBy(e => e.Weight)
                     .ThenBy(e => e.RecordedAt)
                     .Take(ofSource.Count - cap))
        {
            Entries.Remove(doomed);
        }
    }

    private static string DescribeElapsed(TimeSpan elapsed) => elapsed.TotalDays switch
    {
        >= 14 => "a long while ago",
        >= 2 => $"{(int)elapsed.TotalDays} days ago",
        >= 1 => "yesterday",
        _ => elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h ago" : "moments ago"
    };
}

/// <summary>A single thing an NPC recalls about a player.</summary>
public class NpcMemoryEntry
{
    /// <summary>One short line in the NPC's own frame of reference.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>0-100 significance. Drives both prompt ordering and how long it survives.</summary>
    public int Weight { get; set; } = 40;

    /// <summary>Core memories never fade: romance, betrayal, a crime witnessed, a life saved.</summary>
    public bool IsCore { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this was witnessed first-hand or picked up as gossip.</summary>
    public NpcMemorySource Source { get; set; } = NpcMemorySource.Direct;
}

/// <summary>Something an NPC offered and has not yet delivered.</summary>
public class NpcPromise
{
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset MadeAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How an NPC came by a memory.</summary>
public enum NpcMemorySource
{
    /// <summary>Witnessed or experienced directly.</summary>
    Direct,

    /// <summary>Heard from someone else — lower fidelity, forgotten sooner.</summary>
    Gossip
}
