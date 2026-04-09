using GAE.Core.Registry;

namespace GAE.Core.Models;

/// <summary>
/// Author-defined theme and plot scaffolding for a Blind Adventure run.
/// This is intentionally small so narrator-driven room generation can stay coherent
/// without locking the player into a rigid quest script.
/// </summary>
public class StorylineContext : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Setting { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public List<string> PlotBeats { get; set; } = [];
    public string StartingRoomDescription { get; set; } = string.Empty;
    public int MaxRooms { get; set; } = 20;
}