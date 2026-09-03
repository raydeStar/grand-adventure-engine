namespace GAE.Core.Models;

/// <summary>
/// Describes an authoritative Dungeon Master hold that blocks mutating player turns across every
/// transport while preserving safe character-reference commands.
/// </summary>
public class PlayerCommandHold
{
    public string PlayerId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string HeldBy { get; set; } = string.Empty;
    public DateTimeOffset HeldAt { get; set; }
    public string? SourceActionId { get; set; }
}
