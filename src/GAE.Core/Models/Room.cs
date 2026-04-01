namespace GAE.Core.Models;

public class Room
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, string> Exits { get; set; } = new();
    public List<Npc> Npcs { get; set; } = [];
    public List<InventoryItem> Items { get; set; } = [];
    public List<string> EnvironmentTags { get; set; } = [];
    public bool IsDiscovered { get; set; }
    public string? AsciiArt { get; set; }
    public DateTimeOffset? DiscoveredAt { get; set; }
}
