namespace GAE.Core.Models;

/// <summary>
/// A frozen snapshot of CYOA state captured at a save point — used for death rewind and voluntary load.
/// </summary>
public class CyoaSaveSnapshot
{
    public string NodeId { get; set; } = string.Empty;
    public string NarrationText { get; set; } = string.Empty;
    public CyoaHealthLevel Health { get; set; }
    public List<string> Inventory { get; set; } = [];
    public List<CyoaChoice> Choices { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int ChoiceCountAtSave { get; set; }
}
