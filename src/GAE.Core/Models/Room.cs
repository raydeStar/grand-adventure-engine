using System.Text.Json;

namespace GAE.Core.Models;

public class Room
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];
    public Dictionary<string, string> Exits { get; set; } = new();
    public List<Npc> Npcs { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];
    public List<string> EnvironmentTags { get; set; } = [];
    public bool IsDiscovered { get; set; }
    public string? AsciiArt { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }

    /// <summary>Creates a deep clone of this room for per-player instancing.</summary>
    public Room DeepClone(string newId)
    {
        var json = JsonSerializer.Serialize(this);
        var clone = JsonSerializer.Deserialize<Room>(json)!;
        clone.Id = newId;
        return clone;
    }
}