namespace GAE.Core.Models;

public class SpellVetResponse
{
    public bool Approved { get; set; }
    public string? RejectionReason { get; set; }
    public string SpellName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "damage";
    public string TargetType { get; set; } = "enemy";
    public int BasePower { get; set; }
    public int MpCost { get; set; }
    public string Narration { get; set; } = string.Empty;
}
