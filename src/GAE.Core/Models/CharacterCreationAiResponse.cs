namespace GAE.Core.Models;

/// <summary>Response from the AI character creation narrator.</summary>
public class CharacterCreationAiResponse
{
    public string? Name { get; set; }
    public string Gender { get; set; } = string.Empty;
    public string Race { get; set; } = "Human";
    public string Class { get; set; } = "Fighter";
    public List<string> PersonalItems { get; set; } = [];
    public List<string> StatOrder { get; set; } = ["str", "con", "dex", "wis", "cha", "int"];
    public string Backstory { get; set; } = string.Empty;
    public string? FollowUpQuestion { get; set; }
}