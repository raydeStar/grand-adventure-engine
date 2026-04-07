using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Registry;

/// <summary>
/// Singleton service that holds all content registries. Loaded from YAML seeds at boot,
/// refreshable from wiki or admin API at runtime.
/// </summary>
public class ContentRegistryService : IContentRegistryService
{
    private readonly ILogger<ContentRegistryService> _logger;

    public IContentRegistry<SpellDefinition> Spells { get; } = new ContentRegistry<SpellDefinition>();
    public IContentRegistry<ClassDefinition> Classes { get; } = new ContentRegistry<ClassDefinition>();
    public IContentRegistry<RaceDefinition> Races { get; } = new ContentRegistry<RaceDefinition>();
    public IContentRegistry<ItemTemplate> Items { get; } = new ContentRegistry<ItemTemplate>();
    public IContentRegistry<MonsterTemplate> Monsters { get; } = new ContentRegistry<MonsterTemplate>();
    public IContentRegistry<QuestDefinition> Quests { get; } = new ContentRegistry<QuestDefinition>();
    public IContentRegistry<LoreEntry> LoreEntries { get; } = new ContentRegistry<LoreEntry>();
    public IContentRegistry<NarratorPreset> NarratorPresets { get; } = new ContentRegistry<NarratorPreset>();

    public ContentRegistryService(ILogger<ContentRegistryService> logger)
    {
        _logger = logger;
    }

    public int GetImprovisedSpellCap(string className, int level)
    {
        var cls = Classes.FindByName(className);
        if (cls is null)
        {
            // Unknown class — use conservative default: level 1 = cap 1, +1 per 2 levels
            return Math.Max(1, (level + 1) / 2);
        }

        if (!cls.CanCastSpells)
        {
            // Non-caster classes get a very low cap — they can try, but it's hard
            return Math.Max(1, level / 3);
        }

        // Use class-defined caps, clamped to array bounds
        var index = Math.Clamp(level - 1, 0, cls.ImprovisedSpellCap.Count - 1);
        return cls.ImprovisedSpellCap[index];
    }

    public IReadOnlyList<SpellDefinition> GetSpellsForClass(string className, int level)
    {
        var cls = Classes.FindByName(className);
        if (cls is null)
            return Spells.GetAll().Where(s => s.RequiredLevel <= level).ToList();

        return Spells.GetAll()
            .Where(s => s.RequiredLevel <= level &&
                        (s.RequiredClasses.Count == 0 ||
                         s.RequiredClasses.Any(rc => rc.Equals(className, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    public SpellValidationResult ValidateSpellCast(string spellIdOrName, string playerClass, int playerLevel, int playerMp)
    {
        var spell = Spells.GetById(spellIdOrName) ?? Spells.FindByName(spellIdOrName);
        if (spell is null)
            return new SpellValidationResult { IsValid = false, FailureReason = "not_registered" };

        if (spell.RequiredClasses.Count > 0 &&
            !spell.RequiredClasses.Any(rc => rc.Equals(playerClass, StringComparison.OrdinalIgnoreCase)))
            return new SpellValidationResult { IsValid = false, Spell = spell, FailureReason = $"Your class ({playerClass}) cannot cast {spell.Name}." };

        if (playerLevel < spell.RequiredLevel)
            return new SpellValidationResult { IsValid = false, Spell = spell, FailureReason = $"You need to be level {spell.RequiredLevel} to cast {spell.Name}. You are level {playerLevel}." };

        if (playerMp < spell.ManaCost)
            return new SpellValidationResult { IsValid = false, Spell = spell, FailureReason = $"Not enough mana. {spell.Name} costs {spell.ManaCost} MP, you have {playerMp} MP." };

        return new SpellValidationResult { IsValid = true, Spell = spell };
    }

    public void LogRegistrySummary()
    {
        _logger.LogInformation("Content registries loaded: {Spells} spells, {Classes} classes, {Races} races, {Items} items, {Monsters} monsters, {Quests} quests, {LoreEntries} lore entries, {NarratorPresets} narrator presets",
            Spells.Count, Classes.Count, Races.Count, Items.Count, Monsters.Count, Quests.Count, LoreEntries.Count, NarratorPresets.Count);
    }
}
