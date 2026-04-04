namespace GAE.Core.Models;

/// <summary>
/// Captures a single LLM prompt/response exchange for training data and audit.
/// </summary>
public class ConversationLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Operation { get; set; } = string.Empty;
    public string? PlayerId { get; set; }
    public string? RoomId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int MaxTokens { get; set; }
    public long LatencyMs { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
