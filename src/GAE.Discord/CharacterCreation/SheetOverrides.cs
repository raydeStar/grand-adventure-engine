using System.Globalization;
using System.Text.RegularExpressions;
using GAE.Core.Models;

namespace GAE.Discord.CharacterCreation;

/// <summary>
/// Detects explicit "change X to Y" requests from players and applies them
/// to the AI-generated character sheet, even if the model didn't comply.
/// </summary>
public static partial class SheetOverrides
{
    /// <summary>
    /// If the player said "make the race X" or "change class to Y", force the
    /// override on the current AI response. Also prevents the AI from reverting
    /// previous custom values back to defaults.
    /// </summary>
    public static void Apply(string playerInput, CharacterCreationAiResponse current, CharacterCreationAiResponse previous)
    {
        var lower = playerInput.ToLowerInvariant();

        var raceMatch = RacePattern().Match(lower);
        if (raceMatch.Success)
        {
            var newRace = ExtractCapture(raceMatch);
            current.Race = ToTitleCase(newRace);
        }

        var classMatch = ClassPattern().Match(lower);
        if (classMatch.Success)
        {
            var newClass = ExtractCapture(classMatch);
            current.Class = ToTitleCase(newClass);
        }

        var nameMatch = NamePattern().Match(lower);
        if (nameMatch.Success)
        {
            var newName = ExtractCapture(nameMatch);
            current.Name = ToTitleCase(newName);
        }

        // Prevent AI from silently reverting custom values back to defaults
        if (!raceMatch.Success && current.Race == "Human" && previous.Race != "Human")
            current.Race = previous.Race;
        if (!classMatch.Success && current.Class == "Fighter" && previous.Class != "Fighter")
            current.Class = previous.Class;
        if (!nameMatch.Success && current.Name is null && previous.Name is not null)
            current.Name = previous.Name;
    }

    private static string ExtractCapture(Match match)
    {
        for (int i = 1; i < match.Groups.Count; i++)
            if (match.Groups[i].Success)
                return match.Groups[i].Value.Trim().TrimEnd('.');
        return string.Empty;
    }

    private static string ToTitleCase(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim());

    // "make race X", "change race to X", "set race to X", "race: X", "make the race X"
    [GeneratedRegex(@"(?:make|change|set)\s+(?:the\s+)?race\s+(?:to\s+)?(.+)|^race[:\s]+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex RacePattern();

    // "make class X", "change class to X", "set class to X", "class: X"
    [GeneratedRegex(@"(?:make|change|set)\s+(?:the\s+)?class\s+(?:to\s+)?(.+)|^class[:\s]+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassPattern();

    // "make name X", "change name to X", "set name to X", "name: X"
    [GeneratedRegex(@"(?:make|change|set)\s+(?:the\s+)?name\s+(?:to\s+)?(.+)|^name[:\s]+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}
