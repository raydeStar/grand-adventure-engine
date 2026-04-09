namespace GAE.Core.Models;

/// <summary>
/// Tracks the state of an active Blind Adventure run.
/// Stored on the player and serialized/persisted with PlayerCharacter.
/// </summary>
public class BlindAdventureSession
{
    /// <summary>The storyline driving this adventure.</summary>
    public StorylineContext Storyline { get; set; } = new();

    /// <summary>IDs of rooms the player has visited during this adventure.</summary>
    public List<string> VisitedRoomIds { get; set; } = [];

    /// <summary>Short summaries of visited rooms (for narrator context).</summary>
    public List<string> VisitedRoomSummaries { get; set; } = [];

    /// <summary>Number of rooms generated so far (including starting room).</summary>
    public int RoomsGenerated { get; set; }

    /// <summary>Number of plot beats the narrator has woven in so far.</summary>
    public int PlotBeatsDelivered { get; set; }

    /// <summary>Notable events for the conclusion summary (e.g. "defeated the cellar spider").</summary>
    public List<string> KeyEvents { get; set; } = [];

    /// <summary>When the adventure started.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The room the player was in before starting the adventure (for restoration).</summary>
    public string PreviousRoomId { get; set; } = string.Empty;

    /// <summary>Remaining rooms before the adventure must conclude.</summary>
    public int RoomsRemaining => Math.Max(0, Storyline.MaxRooms - RoomsGenerated);

    /// <summary>The next plot beat to weave in, or null if all have been delivered.</summary>
    public string? NextPlotBeat =>
        PlotBeatsDelivered < Storyline.PlotBeats.Count
            ? Storyline.PlotBeats[PlotBeatsDelivered]
            : null;
}
