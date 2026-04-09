namespace GAE.Core.Models;

public class CharacterConcept
{
    public string PlayerDiscordId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public string Backstory { get; set; } = string.Empty;
    public List<string> PersonalItems { get; set; } = [];
    public StatAllocationMethod StatMethod { get; set; } = StatAllocationMethod.StandardArray;
    public Dictionary<string, int>? ManualStats { get; set; }

    /// <summary>AI-suggested starting gold. Null = use rules default.</summary>
    public int? StartingGold { get; set; }
}

public enum StatAllocationMethod
{
    StandardArray,
    Roll4d6DropLowest,
    FlatValue,
    Manual
}
