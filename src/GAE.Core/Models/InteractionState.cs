namespace GAE.Core.Models;

public class InteractionState
{
    public InteractionMode Mode { get; set; } = InteractionMode.Explore;
    public string? Target { get; set; }
    public List<string> Context { get; set; } = [];
    public int TurnCount { get; set; }
    public string? NpcDisposition { get; set; }
    public bool CanLeave { get; set; } = true;
    public string? LeaveConsequence { get; set; }

    /// <summary>Max context entries kept before oldest are trimmed.</summary>
    public const int MaxContextEntries = 20;

    public void AppendContext(string entry)
    {
        Context.Add(entry);
        while (Context.Count > MaxContextEntries)
            Context.RemoveAt(0);
        TurnCount++;
    }

    public void Reset()
    {
        Mode = InteractionMode.Explore;
        Target = null;
        Context.Clear();
        TurnCount = 0;
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
    Event
}
