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

        // Stat overrides: "change str to 18", "set strength to 15", etc.
        ApplyStatOverrides(playerInput, current);

        // Prevent AI from silently reverting custom values back to defaults
        if (!raceMatch.Success && current.Race == "Human" && previous.Race != "Human")
            current.Race = previous.Race;
        if (!classMatch.Success && current.Class == "Fighter" && previous.Class != "Fighter")
            current.Class = previous.Class;
        if (!nameMatch.Success && current.Name is null && previous.Name is not null)
            current.Name = previous.Name;
    }

    /// <summary>
    /// Apply overrides directly to a single AI response (used when narrator is unavailable).
    /// Returns true if any override was applied.
    /// </summary>
    public static bool ApplyDirect(string playerInput, CharacterCreationAiResponse response)
    {
        bool changed = false;
        var lower = playerInput.ToLowerInvariant();

        var raceMatch = RacePattern().Match(lower);
        if (raceMatch.Success) { response.Race = ToTitleCase(ExtractCapture(raceMatch)); changed = true; }

        var classMatch = ClassPattern().Match(lower);
        if (classMatch.Success) { response.Class = ToTitleCase(ExtractCapture(classMatch)); changed = true; }

        var nameMatch = NamePattern().Match(lower);
        if (nameMatch.Success) { response.Name = ToTitleCase(ExtractCapture(nameMatch)); changed = true; }

        if (ApplyStatOverrides(playerInput, response)) changed = true;

        return changed;
    }

    /// <summary>Parse stat change requests and reorder the stat priority list.</summary>
    /// <remarks>
    /// StatOrder is a List&lt;string&gt; like ["str","con","dex","wis","cha","int"].
    /// Position 0 gets 15, position 1 gets 14, etc. from the standard array.
    /// "change str to 15" means move "str" to position 0.
    /// </remarks>
    private static bool ApplyStatOverrides(string playerInput, CharacterCreationAiResponse response)
    {
        if (response.StatOrder is null || response.StatOrder.Count < 6) return false;

        bool changed = false;
        var standardArray = new[] { 15, 14, 13, 12, 10, 8 };

        foreach (Match m in StatChangePattern().Matches(playerInput))
        {
            var statRaw = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value).Trim().ToLowerInvariant();
            var valStr = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value).Trim();
            if (!int.TryParse(valStr, out int targetVal)) continue;

            var stat = NormalizeStatName(statRaw);
            if (stat is null) continue;

            // Find which position in the standard array has the target value
            int targetPosition = Array.IndexOf(standardArray, targetVal);
            if (targetPosition < 0) continue; // not a valid standard array value (8,10,12,13,14,15)

            // Find where this stat currently is in the order
            int currentPosition = response.StatOrder.IndexOf(stat);
            if (currentPosition < 0 || currentPosition == targetPosition) continue;

            // Swap: move our stat to the target position, displacing what was there
            response.StatOrder.RemoveAt(currentPosition);
            response.StatOrder.Insert(targetPosition, stat);
            changed = true;
        }
        return changed;
    }

    private static string? NormalizeStatName(string raw) => raw switch
    {
        "str" or "strength" => "str",
        "dex" or "dexterity" => "dex",
        "con" or "constitution" => "con",
        "int" or "intelligence" => "int",
        "wis" or "wisdom" => "wis",
        "cha" or "charisma" => "cha",
        _ => null
    };

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

    // "change str to 18", "set strength to 15", "make con 14", "str 15", "str: 15"
    [GeneratedRegex(@"(?:make|change|set)\s+(?:the\s+)?(str|strength|dex|dexterity|con|constitution|int|intelligence|wis|wisdom|cha|charisma)\s+(?:to\s+)?(\d+)|(str|strength|dex|dexterity|con|constitution|int|intelligence|wis|wisdom|cha|charisma)[:\s]+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatChangePattern();
}
