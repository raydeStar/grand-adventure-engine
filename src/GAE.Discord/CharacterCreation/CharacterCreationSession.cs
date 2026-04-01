using GAE.Core.Models;

namespace GAE.Discord.CharacterCreation;

public class CharacterCreationSession
{
    private readonly string _discordId;
    private CreationStep _step = CreationStep.Name;

    public string? Name { get; private set; }
    public string? Race { get; private set; }
    public string? Class { get; private set; }
    public string? Backstory { get; private set; }
    public StatAllocationMethod StatMethod { get; private set; } = StatAllocationMethod.StandardArray;
    public bool IsComplete { get; private set; }

    public CharacterCreationSession(string discordId)
    {
        _discordId = discordId;
    }

    public string ProcessInput(string input)
    {
        switch (_step)
        {
            case CreationStep.Name:
                Name = input;
                _step = CreationStep.Race;
                return """
                    **Step 2: Choose your race.**
                    Options: Human, Elf, Dwarf, Halfling, Orc, Tiefling
                    (Or type any race you'd like!)
                    """;

            case CreationStep.Race:
                Race = input;
                _step = CreationStep.Class;
                return """
                    **Step 3: Choose your class.**
                    Options: Warrior, Mage, Rogue, Cleric, Ranger, Bard
                    (Or type any class you'd like!)
                    """;

            case CreationStep.Class:
                Class = input;
                _step = CreationStep.Stats;
                return """
                    **Step 4: How would you like to assign stats?**
                    1. **Standard Array** (15, 14, 13, 12, 10, 8) — balanced and predictable
                    2. **Roll 4d6 Drop Lowest** — random but potentially powerful
                    3. **Flat 10s** — all stats start at 10
                    (Type the number or name)
                    """;

            case CreationStep.Stats:
                StatMethod = input.Trim().ToLowerInvariant() switch
                {
                    "1" or "standard" or "standard array" => StatAllocationMethod.StandardArray,
                    "2" or "roll" or "4d6" or "roll 4d6" => StatAllocationMethod.Roll4d6DropLowest,
                    "3" or "flat" or "flat 10" or "flat 10s" => StatAllocationMethod.FlatValue,
                    _ => StatAllocationMethod.StandardArray
                };
                _step = CreationStep.Backstory;
                return """
                    **Step 5: Tell us about your character's backstory.**
                    (Write a brief description, or type "skip" to have one generated for you)
                    """;

            case CreationStep.Backstory:
                Backstory = input.Equals("skip", StringComparison.OrdinalIgnoreCase) ? "" : input;
                IsComplete = true;
                return "Creating your character...";

            default:
                return "Something went wrong. Please try `!create` again.";
        }
    }

    public CharacterConcept ToConcept() => new()
    {
        PlayerDiscordId = _discordId,
        Name = Name ?? "Unnamed",
        Race = Race ?? "Human",
        Class = Class ?? "Warrior",
        Backstory = Backstory ?? "",
        StatMethod = StatMethod
    };

    private enum CreationStep
    {
        Name,
        Race,
        Class,
        Stats,
        Backstory
    }
}
