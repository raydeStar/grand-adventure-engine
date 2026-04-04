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
            if (room.Items is null) continue;
            foreach (var item in room.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                if (registry.Exists(item.Id)) continue;

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
                    StatBonuses = item.StatBonuses ?? new(),
                    Tags = []
                });
                count++;
            }

            // Also pull items from NPC loot tables
            if (room.Npcs is null) continue;
            foreach (var npc in room.Npcs)
            {
                if (npc.Loot is null) continue;
                foreach (var loot in npc.Loot)
                {
                    if (string.IsNullOrWhiteSpace(loot.Id) || registry.Exists(loot.Id)) continue;
                    registry.Register(new ItemTemplate
                    {
                        Id = loot.Id,
                        Name = loot.Name ?? loot.Id,
                        Description = loot.Description,
                        Type = ParseItemType(loot.Type),
                        DamageDice = loot.DamageDice,
                        DamageStat = loot.DamageStat,
                        ArmorValue = loot.ArmorValue,
                        IsEquippable = loot.IsEquippable ?? InventoryItem.IsEquippableType(ParseItemType(loot.Type)),
                        IsConsumable = loot.IsConsumable ?? false,
                        IsTwoHanded = loot.IsTwoHanded ?? false,
                        Effect = loot.Effect,
                        Value = loot.Value,
                        StatBonuses = loot.StatBonuses ?? new(),
                        Tags = []
                    });
                    count++;
                }
            }
        }

        logger?.LogInformation("Loaded {Count} item templates from lore seed", count);
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
    }
}
