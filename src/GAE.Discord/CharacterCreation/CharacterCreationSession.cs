using System.Text.RegularExpressions;
using GAE.Core.Models;

namespace GAE.Discord.CharacterCreation;

/// <summary>
/// Fallback character creation when the AI narrator is unavailable.
/// Tries to parse a single free-form block first. Only falls back to
/// step-by-step if the description is too vague.
/// </summary>
public partial class CharacterCreationSession
{
    private readonly string _discordId;
    private CreationStep _step = CreationStep.FreeForm;

    public string? Name { get; private set; }
    public string? Race { get; private set; }
    public string? Class { get; private set; }
    public string? Backstory { get; private set; }
    public bool IsComplete { get; private set; }

    public CharacterCreationSession(string discordId)
    {
        _discordId = discordId;
    }

    public string ProcessInput(string input)
    {
        switch (_step)
        {
            case CreationStep.FreeForm:
                // Try to extract everything from a single block of text
                ParseFreeForm(input);

                if (Name is not null && Race is not null && Class is not null)
                {
                    // Got everything — use the full input as backstory if we don't have one
                    Backstory ??= input;
                    IsComplete = true;
                    return "Creating your character...";
                }

                // Got some info — ask for what's missing
                if (Name is null)
                {
                    _step = CreationStep.Name;
                    var have = BuildHaveSummary();
                    return $"Got it!{have}\nWhat's your character's **name**?";
                }
                if (Race is null)
                {
                    _step = CreationStep.Race;
                    var have = BuildHaveSummary();
                    return $"Got it!{have}\n**What are you?** *(Human, Elf, Duck, Mooninite — anything goes)*";
                }
                if (Class is null)
                {
                    _step = CreationStep.Class;
                    var have = BuildHaveSummary();
                    return $"Got it!{have}\n**What do you do?** *(Fighter, Mage, Hitman, Cheese Wizard — anything goes)*";
                }

                // Shouldn't reach here but just in case
                _step = CreationStep.Name;
                return "Tell me more! What's your character's **name**?";

            case CreationStep.Name:
                Name = input.Trim();
                if (Race is null)
                {
                    _step = CreationStep.Race;
                    return "**What are you?** *(Human, Elf, Duck, Mooninite — anything goes)*";
                }
                if (Class is null)
                {
                    _step = CreationStep.Class;
                    return "**What do you do?** *(Fighter, Mage, Hitman, Cheese Wizard — anything goes)*";
                }
                Backstory ??= "";
                IsComplete = true;
                return "Creating your character...";

            case CreationStep.Race:
                Race = input.Trim();
                if (Class is null)
                {
                    _step = CreationStep.Class;
                    return "**What do you do?** *(Fighter, Mage, Hitman, Cheese Wizard — anything goes)*";
                }
                Backstory ??= "";
                IsComplete = true;
                return "Creating your character...";

            case CreationStep.Class:
                Class = input.Trim();
                Backstory ??= "";
                IsComplete = true;
                return "Creating your character...";

            default:
                return "Something went wrong. Please try `/create` again.";
        }
    }

    public CharacterConcept ToConcept() => new()
    {
        PlayerDiscordId = _discordId,
        Name = Name ?? "Unnamed",
        Race = Race ?? "Human",
        Class = Class ?? "Warrior",
        Backstory = Backstory ?? "",
        StatMethod = StatAllocationMethod.StandardArray
    };

    private void ParseFreeForm(string input)
    {
        var text = input.Trim();

        // Name: "my name is X", "I'm X", "I am X", "name: X", "called X"
        var nameMatch = NamePattern().Match(text);
        if (nameMatch.Success)
            Name = ExtractFirst(nameMatch);

        // Race: "I am a/an X <class>", "I'm a/an X", "race: X"
        // Also detect known race-like words anywhere
        var raceMatch = RaceExplicitPattern().Match(text);
        if (raceMatch.Success)
        {
            Race = ExtractFirst(raceMatch);
        }
        else
        {
            // Try to find race-like nouns: "I am a duck", "I'm an orc"
            var iAmMatch = IAmAPattern().Match(text);
            if (iAmMatch.Success)
            {
                var words = iAmMatch.Groups[1].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 1)
                {
                    // First word after "I am a" is likely the race (or an adjective + race)
                    // If it looks like a class word, skip it for race
                    var candidate = words[0];
                    if (!IsLikelyClassWord(candidate))
                        Race = ToTitleCase(candidate);
                    else if (words.Length > 1 && !IsLikelyClassWord(words[1]))
                        Race = ToTitleCase(words[1]);
                }
            }
        }

        // Class: "class: X", explicit class keywords, or role-like words
        var classMatch = ClassExplicitPattern().Match(text);
        if (classMatch.Success)
        {
            Class = ExtractFirst(classMatch);
        }
        else
        {
            // Look for class-like words in the text
            var classWord = FindClassWord(text);
            if (classWord is not null)
                Class = ToTitleCase(classWord);
        }

        // Backstory: use the full text
        if (text.Length > 20)
            Backstory = text;
    }

    private static string? FindClassWord(string text)
    {
        var lower = text.ToLowerInvariant();
        // Check for explicit class words
        string[] classWords = [
            "fighter", "warrior", "knight", "barbarian", "paladin",
            "mage", "wizard", "sorcerer", "warlock",
            "rogue", "thief", "assassin", "hitman",
            "cleric", "priest", "healer", "monk",
            "ranger", "hunter", "archer",
            "bard", "skald", "pirate", "necromancer", "druid"
        ];

        foreach (var word in classWords)
        {
            if (lower.Contains(word))
                return word;
        }
        return null;
    }

    private static bool IsLikelyClassWord(string word) =>
        FindClassWordExact(word.ToLowerInvariant());

    private static bool FindClassWordExact(string lower) => lower switch
    {
        "fighter" or "warrior" or "knight" or "barbarian" or "paladin" or
        "mage" or "wizard" or "sorcerer" or "warlock" or
        "rogue" or "thief" or "assassin" or "hitman" or
        "cleric" or "priest" or "healer" or "monk" or
        "ranger" or "hunter" or "archer" or
        "bard" or "skald" or "pirate" or "necromancer" or "druid" => true,
        _ => false
    };

    private string BuildHaveSummary()
    {
        var parts = new List<string>();
        if (Name is not null) parts.Add($"Name: **{Name}**");
        if (Race is not null) parts.Add($"Race: **{Race}**");
        if (Class is not null) parts.Add($"Class: **{Class}**");
        return parts.Count > 0 ? " " + string.Join(" | ", parts) : "";
    }

    private static string ExtractFirst(Match match)
    {
        for (int i = 1; i < match.Groups.Count; i++)
            if (match.Groups[i].Success)
                return ToTitleCase(match.Groups[i].Value.Trim().TrimEnd('.', ',', '!'));
        return string.Empty;
    }

    private static string ToTitleCase(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());

    // "my name is X", "I'm called X", "name: X" — stops at punctuation, "and", "I am/I'm"
    [GeneratedRegex(@"(?:my\s+name\s+is|i'?m\s+called|name[:\s]+)\s*(\w+(?:\s+\w+)??)(?:\s*[,.\n!]|\s+and\s+|\s+i'?m?\s+|\s+i\s+am|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    // "race: X", "race is X"
    [GeneratedRegex(@"race[:\s]+\s*([^,.\n!]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RaceExplicitPattern();

    // "I am a/an X", "I'm a/an X"
    [GeneratedRegex(@"i(?:'m|\s+am)\s+(?:a|an)\s+(.+?)(?:\.|,|!|$)", RegexOptions.IgnoreCase)]
    private static partial Regex IAmAPattern();

    // "class: X", "class is X"
    [GeneratedRegex(@"class[:\s]+\s*([^,.\n!]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ClassExplicitPattern();

    private enum CreationStep
    {
        FreeForm,
        Name,
        Race,
        Class
    }
}
