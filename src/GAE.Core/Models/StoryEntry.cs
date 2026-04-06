namespace GAE.Core.Models;

public class StoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ActionId { get; set; } = string.Empty;
    public string RawInput { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string WorldId { get; set; } = WorldDefaults.DefaultWorldId;
    public string RoomId { get; set; } = string.Empty;
    public string MechanicalSummary { get; set; } = string.Empty;
    public string Narration { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
