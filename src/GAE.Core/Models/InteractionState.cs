namespace GAE.Core.Models;

public class InteractionState
{
    public InteractionMode Mode { get; set; } = InteractionMode.Explore;
    public string? Target { get; set; }

    /// <summary>Recent turns, kept verbatim. Trimmed from the front once full.</summary>
    public List<string> Context { get; set; } = [];

    /// <summary>
    /// Turns that must survive the window: offers, promises, agreed prices, threats acted on.
    ///
    /// The plain window is a fixed-size queue, so in a long conversation the earliest turns fall out.
    /// That is where commitments live — an NPC offering a secret in exchange for a drink does it in the
    /// first few exchanges — and losing them is why an NPC could take payment and then never deliver.
    /// Pinned turns are exempt from eviction and are carried into long-term memory when the
    /// conversation ends.
    /// </summary>
    public List<string> PinnedContext { get; set; } = [];

    /// <summary>
    /// A compact running account of turns that have aged out of the verbatim window, so an
    /// hour-long conversation still has a beginning rather than starting fresh every twenty turns.
    /// </summary>
    public string? RunningSummary { get; set; }
    public int TurnCount { get; set; }
    /// <summary>Counts completed player turns separately from raw context entries.</summary>
    public int PlayerTurnCount { get; set; }
    public string? NpcDisposition { get; set; }
    public bool CanLeave { get; set; } = true;
    public string? LeaveConsequence { get; set; }

    public int CurrentTurnNumber => Math.Max(1, PlayerTurnCount);

    /// <summary>Max context entries kept before oldest are trimmed.</summary>
    public const int MaxContextEntries = 20;

    /// <summary>Longest the running summary is allowed to grow, in characters.</summary>
    public const int MaxRunningSummaryLength = 700;

    /// <summary>Most commitments tracked before the oldest is dropped.</summary>
    public const int MaxPinnedEntries = 6;

    public void AppendContext(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;

        // Commitments are pinned as well as queued, so they outlive the window.
        if (LooksLikeCommitment(entry))
        {
            var trimmed = entry.Trim();
            if (!PinnedContext.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                PinnedContext.Add(trimmed);
                while (PinnedContext.Count > MaxPinnedEntries)
                    PinnedContext.RemoveAt(0);
            }
        }

        Context.Add(entry);

        // Anything leaving the window is folded into the summary rather than discarded.
        while (Context.Count > MaxContextEntries)
        {
            FoldIntoRunningSummary(Context[0]);
            Context.RemoveAt(0);
        }

        TurnCount++;
    }

    /// <summary>
    /// Recognises a turn that creates an obligation. Deliberately generous: wrongly keeping a line is
    /// cheap, whereas dropping a promise is the bug this exists to prevent.
    /// </summary>
    public static bool LooksLikeCommitment(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;

        var text = entry.ToLowerInvariant();
        string[] cues =
        [
            "i'll tell", "ill tell", "i will tell", "promise", "promised", "swear", "deal",
            "in exchange", "if you bring", "if you buy", "buy me", "owe", "owed", "agreed",
            "i'll show", "i will show", "i'll give", "i will give", "meet me", "come back",
            "pay", "paid", "coin", "gold", "price", "cost", "bargain", "trade you", "my word"
        ];
        return cues.Any(cue => text.Contains(cue, StringComparison.Ordinal));
    }

    /// <summary>
    /// Appends an aged-out turn to the running summary, keeping it bounded. Rule-based on purpose: it
    /// runs on every turn and must never depend on the narrator being reachable.
    /// </summary>
    private void FoldIntoRunningSummary(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;

        var clause = entry.Trim().TrimEnd('.', ' ');
        if (clause.Length > 90)
            clause = clause[..90].TrimEnd() + "...";

        RunningSummary = string.IsNullOrWhiteSpace(RunningSummary)
            ? clause
            : RunningSummary + "; " + clause;

        // Trim from the front so the most recent aged-out turns are the ones retained.
        if (RunningSummary.Length > MaxRunningSummaryLength)
        {
            var excess = RunningSummary.Length - MaxRunningSummaryLength;
            var cut = RunningSummary.IndexOf("; ", excess, StringComparison.Ordinal);
            RunningSummary = cut >= 0
                ? "...earlier: " + RunningSummary[(cut + 2)..]
                : RunningSummary[^MaxRunningSummaryLength..];
        }
    }

    /// <summary>Advances the player-facing turn counter for the active interaction.</summary>
    public void AdvancePlayerTurn()
    {
        PlayerTurnCount++;
    }

    public void Reset()
    {
        Mode = InteractionMode.Explore;
        Target = null;
        Context.Clear();
        PinnedContext.Clear();
        RunningSummary = null;
        TurnCount = 0;
        PlayerTurnCount = 0;
        NpcDisposition = null;
        CanLeave = true;
        LeaveConsequence = null;
    }
}

public enum InteractionMode
{
    Explore,
    Conversation,
    Combat,
    Trading,
    Stealth,
    Event,
    BlindAdventure,
    Cyoa
}
