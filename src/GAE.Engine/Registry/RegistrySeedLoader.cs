using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;

namespace GAE.Engine.Registry;

/// <summary>Loads content registry seed files from YAML.</summary>
public static class RegistrySeedLoader
{
    public static void LoadSpells(IContentRegistry<SpellDefinition> registry, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<SpellSeedFile>(yamlContent);
        if (seed?.Spells is null) return;

        foreach (var spell in seed.Spells)
            registry.Register(spell);

        logger?.LogInformation("Loaded {Count} spells from seed", seed.Spells.Count);
    }

    public static void LoadClasses(IContentRegistry<ClassDefinition> registry, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<ClassSeedFile>(yamlContent);
        if (seed?.Classes is null) return;

        foreach (var cls in seed.Classes)
            registry.Register(cls);

        logger?.LogInformation("Loaded {Count} classes from seed", seed.Classes.Count);
    }

    public static void LoadRaces(IContentRegistry<RaceDefinition> registry, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<RaceSeedFile>(yamlContent);
        if (seed?.Races is null) return;

        foreach (var race in seed.Races)
            registry.Register(race);

        logger?.LogInformation("Loaded {Count} races from seed", seed.Races.Count);
    }

    public static void LoadMonsters(IContentRegistry<MonsterTemplate> registry, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<MonsterSeedFile>(yamlContent);
        if (seed?.Monsters is null) return;

        foreach (var m in seed.Monsters)
            registry.Register(m);

        logger?.LogInformation("Loaded {Count} monster templates from seed", seed.Monsters.Count);
    }

    public static void LoadQuests(IContentRegistry<QuestDefinition> registry, IContentRegistry<ItemTemplate> items, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<QuestSeedFile>(yamlContent);
        if (seed?.Quests is null) return;

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var quest in seed.Quests)
        {
            // Duplicate quest ID check
            if (!seenIds.Add(quest.Id))
            {
                errors.Add($"Duplicate quest ID: '{quest.Id}'");
                continue;
            }

            if (string.IsNullOrWhiteSpace(quest.GiverId))
                errors.Add($"Quest '{quest.Id}' has no giver_id");

            // Validate stages
            var stageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stage in quest.Stages)
            {
                if (!stageIds.Add(stage.Id))
                    errors.Add($"Quest '{quest.Id}' has duplicate stage ID: '{stage.Id}'");

                if (stage.NextStageId is not null && !quest.Stages.Any(s => s.Id.Equals(stage.NextStageId, StringComparison.OrdinalIgnoreCase)))
                    errors.Add($"Quest '{quest.Id}' stage '{stage.Id}' references unknown next_stage_id: '{stage.NextStageId}'");

                // Validate objectives
                var objIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var obj in stage.Objectives)
                {
                    if (!objIds.Add(obj.Id))
                        errors.Add($"Quest '{quest.Id}' stage '{stage.Id}' has duplicate objective ID: '{obj.Id}'");

                    if (obj.Type == ObjectiveType.Custom && string.IsNullOrWhiteSpace(obj.CustomCondition))
                        errors.Add($"Quest '{quest.Id}' objective '{obj.Id}' is Custom but has no custom_condition");
                }
            }

            // Check for circular stage references
            if (HasCircularStages(quest))
                errors.Add($"Quest '{quest.Id}' has circular stage references");

            // Validate reward item IDs exist in the item registry
            foreach (var rewardItem in quest.Rewards.Items)
            {
                if (!items.Exists(rewardItem.ItemId))
                    logger?.LogWarning("Quest '{QuestId}' reward references unknown item: '{ItemId}'", quest.Id, rewardItem.ItemId);
            }

            if (errors.Count == 0 || !errors.Any(e => e.Contains(quest.Id)))
                registry.Register(quest);
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                logger?.LogError("Quest seed validation error: {Error}", error);
        }

        logger?.LogInformation("Loaded {Count} quest definitions from seed", registry.Count);
    }

    private static bool HasCircularStages(QuestDefinition quest)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in quest.Stages)
        {
            visited.Clear();
            var current = stage.Id;
            while (current is not null)
            {
                if (!visited.Add(current)) return true;
                current = quest.Stages.FirstOrDefault(s => s.Id.Equals(current, StringComparison.OrdinalIgnoreCase))?.NextStageId;
            }
        }
        return false;
    }

    public static void LoadItems(IContentRegistry<ItemTemplate> registry, string yamlContent, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<ItemSeedFile>(yamlContent);
        if (seed?.Items is null) return;

        int count = 0;
        foreach (var item in seed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || registry.Exists(item.Id)) continue;
            registry.Register(new ItemTemplate
            {
                Id = item.Id,
                Name = item.Name ?? item.Id,
                Description = item.Description,
                Type = ParseItemType(item.Type),
                DamageDice = item.DamageDice,
                DamageStat = item.DamageStat,
                ArmorValue = item.ArmorValue,
                IsEquippable = item.IsEquippable ?? InventoryItem.IsEquippableType(ParseItemType(item.Type)),
                IsConsumable = item.IsConsumable ?? false,
                IsTwoHanded = item.IsTwoHanded ?? false,
                Effect = item.Effect,
                Value = item.Value,
                RequiredLevel = item.RequiredLevel,
                Rarity = item.Rarity ?? "common",
                Tags = item.Tags ?? [],
                StatBonuses = item.StatBonuses ?? new(),
            });
            count++;
        }

        logger?.LogInformation("Loaded {Count} item templates from item seed", count);
    }

    public static void LoadItemsFromLoreSeed(IContentRegistry<ItemTemplate> registry, string loreSeedYaml, ILogger? logger = null)
    {
        var deserializer = BuildDeserializer();
        var seed = deserializer.Deserialize<LoreSeedForItems>(loreSeedYaml);

        int count = 0;
        var allRooms = new List<LoreRoomForItems>();
        if (seed?.StartingRoom is not null) allRooms.Add(seed.StartingRoom);
        if (seed?.Rooms is not null) allRooms.AddRange(seed.Rooms);

        foreach (var room in allRooms)
        {
            if (room.Items is not null)
            {
                foreach (var item in room.Items)
                    RegisterLoreItemIfMissing(registry, item, ref count);
            }

            if (room.Npcs is null) continue;

            foreach (var npc in room.Npcs)
            {
                if (npc.ShopInventory is not null)
                {
                    foreach (var shopItem in npc.ShopInventory)
                        RegisterLoreItemIfMissing(registry, shopItem, ref count);
                }

                if (npc.Loot is null) continue;

                foreach (var loot in npc.Loot)
                    RegisterLoreItemIfMissing(registry, loot, ref count);
            }
        }

        logger?.LogInformation("Loaded {Count} item templates from lore seed", count);
    }

    private static void RegisterLoreItemIfMissing(IContentRegistry<ItemTemplate> registry, LoreItemDto item, ref int count)
    {
        if (string.IsNullOrWhiteSpace(item.Id) || registry.Exists(item.Id))
            return;

        var itemType = ParseItemType(item.Type);
        registry.Register(new ItemTemplate
        {
            Id = item.Id,
            Name = item.Name ?? item.Id,
            Description = item.Description,
            Type = itemType,
            DamageDice = item.DamageDice,
            DamageStat = item.DamageStat,
            ArmorValue = item.ArmorValue,
            IsEquippable = item.IsEquippable ?? InventoryItem.IsEquippableType(itemType),
            IsConsumable = item.IsConsumable ?? false,
            IsTwoHanded = item.IsTwoHanded ?? false,
            Effect = item.Effect,
            Value = item.Value,
            StatBonuses = item.StatBonuses ?? new(),
            RequiredLevel = item.RequiredLevel,
            Rarity = string.IsNullOrWhiteSpace(item.Rarity) ? "common" : item.Rarity,
            Tags = item.Tags ?? []
        });
        count++;
    }

    private static ItemType ParseItemType(string? type) => type?.ToLowerInvariant() switch
    {
        "weapon" => ItemType.Weapon,
        "armor" => ItemType.Armor,
        "shield" => ItemType.Shield,
        "helmet" => ItemType.Helmet,
        "cloak" => ItemType.Cloak,
        "boots" => ItemType.Boots,
        "gloves" => ItemType.Gloves,
        "ring" => ItemType.Ring,
        "amulet" => ItemType.Amulet,
        "bracelet" => ItemType.Bracelet,
        "potion" => ItemType.Potion,
        "scroll" => ItemType.Scroll,
        "key" => ItemType.Key,
        "quest" or "questitem" => ItemType.QuestItem,
        _ => ItemType.Misc
    };

    private static IDeserializer BuildDeserializer() =>
        new DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    // --- Seed file DTOs ---

    private class SpellSeedFile { public List<SpellDefinition>? Spells { get; set; } }
    private class ClassSeedFile { public List<ClassDefinition>? Classes { get; set; } }
    private class RaceSeedFile { public List<RaceDefinition>? Races { get; set; } }
    private class MonsterSeedFile { public List<MonsterTemplate>? Monsters { get; set; } }
    private class ItemSeedFile { public List<LoreItemDto>? Items { get; set; } }
    private class QuestSeedFile { public List<QuestDefinition>? Quests { get; set; } }

    // Lightweight DTOs to extract items from lore-seed without pulling in the full LoreSeed
    private class LoreSeedForItems
    {
        public LoreRoomForItems? StartingRoom { get; set; }
        public List<LoreRoomForItems>? Rooms { get; set; }
    }

    private class LoreRoomForItems
    {
        public List<LoreItemDto>? Items { get; set; }
        public List<LoreNpcForItems>? Npcs { get; set; }
    }

    private class LoreNpcForItems
    {
        public List<LoreItemDto>? ShopInventory { get; set; }
        public List<LoreItemDto>? Loot { get; set; }
    }

    private class LoreItemDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Type { get; set; }
        public int Value { get; set; }
        public string? DamageDice { get; set; }
        public string? DamageStat { get; set; }
        public int ArmorValue { get; set; }
        public bool? IsEquippable { get; set; }
        public bool? IsConsumable { get; set; }
        public bool? IsTwoHanded { get; set; }
        public string? Effect { get; set; }
        public Dictionary<string, int>? StatBonuses { get; set; }
        public int RequiredLevel { get; set; } = 1;
        public string? Rarity { get; set; }
        public List<string>? Tags { get; set; }
    }
}
