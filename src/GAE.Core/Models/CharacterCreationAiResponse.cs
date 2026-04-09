namespace GAE.Core.Models;

/// <summary>Response from the AI character creation narrator.</summary>
public class CharacterCreationAiResponse
{
    public string? Name { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string Race { get; set; } = "Human";
    public string Class { get; set; } = "Fighter";
    public List<string> PersonalItems { get; set; } = [];

    /// <summary>Legacy: stat priority ordering for standard array assignment.</summary>
    public List<string> StatOrder { get; set; } = ["str", "con", "dex", "wis", "cha", "int"];

    /// <summary>
    /// AI-assigned raw stat values. When present, these are used directly instead of StatOrder.
    /// Keys: str, dex, con, int, wis, cha, luck.
    /// </summary>
    public Dictionary<string, int>? Stats { get; set; }

    public string Backstory { get; set; } = string.Empty;
    public string? FollowUpQuestion { get; set; }

    /// <summary>AI-suggested starting gold amount. Null = use rules default.</summary>
    public int? StartingGold { get; set; }
}
