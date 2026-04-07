using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>Aggregate service holding all content registries for the game world.</summary>
public interface IContentRegistryService
{
    IContentRegistry<SpellDefinition> Spells { get; }
    IContentRegistry<ClassDefinition> Classes { get; }
    IContentRegistry<RaceDefinition> Races { get; }
    IContentRegistry<ItemTemplate> Items { get; }
    IContentRegistry<MonsterTemplate> Monsters { get; }
    IContentRegistry<QuestDefinition> Quests { get; }
    IContentRegistry<LoreEntry> LoreEntries { get; }
    IContentRegistry<NarratorPreset> NarratorPresets { get; }

    /// <summary>Get the maximum improvised spell power level a character can attempt.</summary>
    int GetImprovisedSpellCap(string className, int level);

    /// <summary>Get all spells available to a specific class at a given level.</summary>
    IReadOnlyList<SpellDefinition> GetSpellsForClass(string className, int level);

    /// <summary>Validate whether a player can cast a registered spell.</summary>
    SpellValidationResult ValidateSpellCast(string spellIdOrName, string playerClass, int playerLevel, int playerMp);
}

public class SpellValidationResult
{
    public bool IsValid { get; set; }
    public SpellDefinition? Spell { get; set; }
    public string? FailureReason { get; set; }
}
