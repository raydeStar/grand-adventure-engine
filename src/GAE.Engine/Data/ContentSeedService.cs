using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.Registry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Data;

/// <summary>
/// Manages content registry persistence to PostgreSQL.
/// On first startup, seeds from YAML into the DB. On subsequent startups,
/// loads from DB into in-memory ContentRegistryService.
/// Provides write-through methods for admin CRUD operations.
/// </summary>
public class ContentSeedService
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<ContentSeedService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ContentSeedService(IDbContextFactory<GaeDbContext> dbFactory, ILogger<ContentSeedService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Seeds the content_registry table from YAML files if it's empty,
    /// then loads everything from DB into the in-memory registries.
    /// </summary>
    public async Task SeedAndLoadAsync(
        ContentRegistryService registryService,
        string configDir,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hasContent = await db.ContentRegistry.AnyAsync(ct);
        if (!hasContent)
        {
            _logger.LogInformation("Content registry DB is empty — seeding from YAML files");
            await SeedFromYamlAsync(db, configDir, ct);
        }
        else
        {
            await SeedMissingStorylinesAsync(db, configDir, ct);
        }

        // Load all content from DB into in-memory registries
        await LoadRegistriesAsync(db, registryService, ct);
    }

    /// <summary>
    /// Seeds game_config table with GameRulesConfig from YAML if it's empty,
    /// then loads from DB.
    /// </summary>
    public async Task<GameRulesConfig> SeedAndLoadGameRulesAsync(
        string configDir,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await db.GameConfig.FindAsync(new object[] { "game_rules" }, ct);
        if (existing is null)
        {
            _logger.LogInformation("Game rules not in DB — seeding from game-rules.yaml");
            var rulesPath = Path.Combine(configDir, "game-rules.yaml");
            if (File.Exists(rulesPath))
            {
                var yamlContent = await File.ReadAllTextAsync(rulesPath, ct);
                var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                    .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .WithEnumNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var rules = deserializer.Deserialize<GameRulesConfig>(yamlContent);

                existing = new GameConfigEntity
                {
                    Id = "game_rules",
                    Data = JsonSerializer.Serialize(rules, JsonOpts),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.GameConfig.Add(existing);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Seeded game rules into DB");
            }
        }

        if (existing is not null)
        {
            var config = JsonSerializer.Deserialize<GameRulesConfig>(existing.Data, JsonOpts);
            if (config is not null) return config;
        }

        _logger.LogWarning("No game rules found in DB or YAML — using defaults");
        return new GameRulesConfig();
    }

    /// <summary>Persist a content entry to DB (upsert). Called on admin Register.</summary>
    public async Task SaveEntryAsync<T>(string contentType, T entry, CancellationToken ct = default)
        where T : IRegistryEntry
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ContentRegistry
            .FirstOrDefaultAsync(e => e.ContentType == contentType && e.Id == entry.Id, ct);

        if (existing is not null)
        {
            existing.Name = entry.Name;
            existing.Data = JsonSerializer.Serialize(entry, typeof(T), JsonOpts);
        }
        else
        {
            db.ContentRegistry.Add(new ContentRegistryEntity
            {
                ContentType = contentType,
                Id = entry.Id,
                Name = entry.Name,
                Data = JsonSerializer.Serialize(entry, typeof(T), JsonOpts)
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Remove a content entry from DB. Called on admin Remove.</summary>
    public async Task RemoveEntryAsync(string contentType, string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ContentRegistry
            .FirstOrDefaultAsync(e => e.ContentType == contentType && e.Id == id, ct);

        if (existing is not null)
        {
            db.ContentRegistry.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Persist GameRulesConfig to DB.</summary>
    public async Task SaveGameRulesAsync(GameRulesConfig rules, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GameConfig.FindAsync(new object[] { "game_rules" }, ct);

        if (existing is not null)
        {
            existing.Data = JsonSerializer.Serialize(rules, JsonOpts);
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.GameConfig.Add(new GameConfigEntity
            {
                Id = "game_rules",
                Data = JsonSerializer.Serialize(rules, JsonOpts),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private async Task SeedFromYamlAsync(GaeDbContext db, string configDir, CancellationToken ct)
    {
        var entities = new List<ContentRegistryEntity>();

        // Spells
        var spellsPath = Path.Combine(configDir, "spells-seed.yaml");
        if (File.Exists(spellsPath))
        {
            var yaml = await File.ReadAllTextAsync(spellsPath, ct);
            var spells = DeserializeYaml<SpellSeedFile>(yaml)?.Spells;
            if (spells is not null)
            {
                foreach (var s in spells)
                    entities.Add(ToEntity("spell", s));
                _logger.LogInformation("Prepared {Count} spells for DB seeding", spells.Count);
            }
        }

        // Classes
        var classesPath = Path.Combine(configDir, "classes-seed.yaml");
        if (File.Exists(classesPath))
        {
            var yaml = await File.ReadAllTextAsync(classesPath, ct);
            var classes = DeserializeYaml<ClassSeedFile>(yaml)?.Classes;
            if (classes is not null)
            {
                foreach (var c in classes)
                    entities.Add(ToEntity("class", c));
                _logger.LogInformation("Prepared {Count} classes for DB seeding", classes.Count);
            }
        }

        // Races
        var racesPath = Path.Combine(configDir, "races-seed.yaml");
        if (File.Exists(racesPath))
        {
            var yaml = await File.ReadAllTextAsync(racesPath, ct);
            var races = DeserializeYaml<RaceSeedFile>(yaml)?.Races;
            if (races is not null)
            {
                foreach (var r in races)
                    entities.Add(ToEntity("race", r));
                _logger.LogInformation("Prepared {Count} races for DB seeding", races.Count);
            }
        }

        // Items from dungeon-items.yaml
        var dungeonItemsPath = Path.Combine(configDir, "dungeon-items.yaml");
        if (File.Exists(dungeonItemsPath))
        {
            var yaml = await File.ReadAllTextAsync(dungeonItemsPath, ct);
            var items = DeserializeYaml<ItemSeedFile>(yaml)?.Items;
            if (items is not null)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Id)) continue;
                    var template = ToItemTemplate(item);
                    entities.Add(ToEntity("item", template));
                }
                _logger.LogInformation("Prepared {Count} items from dungeon-items for DB seeding", items.Count);
            }
        }

        // Items from lore-seed.yaml (shop items, loot, room items)
        var loreSeedPath = Path.Combine(configDir, "lore-seed.yaml");
        if (File.Exists(loreSeedPath))
        {
            var yaml = await File.ReadAllTextAsync(loreSeedPath, ct);
            var loreItems = ExtractLoreItems(yaml);
            var existingIds = new HashSet<string>(entities.Where(e => e.ContentType == "item").Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            int loreCount = 0;
            foreach (var item in loreItems)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || existingIds.Contains(item.Id)) continue;
                var template = ToItemTemplate(item);
                entities.Add(ToEntity("item", template));
                existingIds.Add(item.Id);
                loreCount++;
            }
            _logger.LogInformation("Prepared {Count} items from lore-seed for DB seeding", loreCount);
        }

        // Monsters
        var monstersPath = Path.Combine(configDir, "monsters.yaml");
        if (File.Exists(monstersPath))
        {
            var yaml = await File.ReadAllTextAsync(monstersPath, ct);
            var monsters = DeserializeYaml<MonsterSeedFile>(yaml)?.Monsters;
            if (monsters is not null)
            {
                foreach (var m in monsters)
                    entities.Add(ToEntity("monster", m));
                _logger.LogInformation("Prepared {Count} monsters for DB seeding", monsters.Count);
            }
        }

        // Quests
        var questsPath = Path.Combine(configDir, "quests.yaml");
        if (File.Exists(questsPath))
        {
            var yaml = await File.ReadAllTextAsync(questsPath, ct);
            var quests = DeserializeYaml<QuestSeedFile>(yaml)?.Quests;
            if (quests is not null)
            {
                foreach (var q in quests)
                    entities.Add(ToEntity("quest", q));
                _logger.LogInformation("Prepared {Count} quests for DB seeding", quests.Count);
            }
        }

        // Lore Entries
        var lorebookPath = Path.Combine(configDir, "lorebook-seed.yaml");
        if (File.Exists(lorebookPath))
        {
            var yaml = await File.ReadAllTextAsync(lorebookPath, ct);
            var loreEntries = DeserializeYaml<LoreEntrySeedFile>(yaml)?.LoreEntries;
            if (loreEntries is not null)
            {
                foreach (var l in loreEntries)
                    entities.Add(ToEntity("lore_entry", l));
                _logger.LogInformation("Prepared {Count} lore entries for DB seeding", loreEntries.Count);
            }
        }

        // Narrator Presets
        var narratorPresetsPath = Path.Combine(configDir, "narrator-presets-seed.yaml");
        if (File.Exists(narratorPresetsPath))
        {
            var yaml = await File.ReadAllTextAsync(narratorPresetsPath, ct);
            var presets = DeserializeYaml<NarratorPresetSeedFile>(yaml)?.NarratorPresets;
            if (presets is not null)
            {
                foreach (var p in presets)
                    entities.Add(ToEntity("narrator_preset", p));
                _logger.LogInformation("Prepared {Count} narrator presets for DB seeding", presets.Count);
            }
        }

        await AppendStorylineEntitiesAsync(entities, configDir, ct);

        if (entities.Count > 0)
        {
            db.ContentRegistry.AddRange(entities);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Seeded {Count} total content entries into DB", entities.Count);
        }
    }

    private async Task SeedMissingStorylinesAsync(GaeDbContext db, string configDir, CancellationToken ct)
    {
        var hasStorylines = await db.ContentRegistry.AnyAsync(e => e.ContentType == "storyline", ct);
        if (hasStorylines)
            return;

        var storylineEntities = new List<ContentRegistryEntity>();
        await AppendStorylineEntitiesAsync(storylineEntities, configDir, ct);
        if (storylineEntities.Count == 0)
            return;

        db.ContentRegistry.AddRange(storylineEntities);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Backfilled {Count} Blind Adventure storylines into DB", storylineEntities.Count);
    }

    private async Task AppendStorylineEntitiesAsync(
        List<ContentRegistryEntity> entities,
        string configDir,
        CancellationToken ct)
    {
        var storylinesDir = Path.Combine(configDir, "blind-storylines");
        if (!Directory.Exists(storylinesDir))
            return;

        var storylines = await StorylineContextLoader.LoadFromDirectoryAsync(storylinesDir, ct);
        foreach (var storyline in storylines.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
            entities.Add(ToEntity("storyline", storyline));

        if (storylines.Count > 0)
            _logger.LogInformation("Prepared {Count} Blind Adventure storylines for DB seeding", storylines.Count);
    }

    private async Task LoadRegistriesAsync(GaeDbContext db, ContentRegistryService registryService, CancellationToken ct)
    {
        var allContent = await db.ContentRegistry.AsNoTracking().ToListAsync(ct);
        _logger.LogInformation("Loading {Count} content entries from DB", allContent.Count);

        foreach (var entity in allContent)
        {
            switch (entity.ContentType)
            {
                case "spell":
                    var spell = JsonSerializer.Deserialize<SpellDefinition>(entity.Data, JsonOpts);
                    if (spell is not null) registryService.Spells.Register(spell);
                    break;
                case "class":
                    var cls = JsonSerializer.Deserialize<ClassDefinition>(entity.Data, JsonOpts);
                    if (cls is not null) registryService.Classes.Register(cls);
                    break;
                case "race":
                    var race = JsonSerializer.Deserialize<RaceDefinition>(entity.Data, JsonOpts);
                    if (race is not null) registryService.Races.Register(race);
                    break;
                case "item":
                    var item = JsonSerializer.Deserialize<ItemTemplate>(entity.Data, JsonOpts);
                    if (item is not null) registryService.Items.Register(item);
                    break;
                case "monster":
                    var monster = JsonSerializer.Deserialize<MonsterTemplate>(entity.Data, JsonOpts);
                    if (monster is not null) registryService.Monsters.Register(monster);
                    break;
                case "quest":
                    var quest = JsonSerializer.Deserialize<QuestDefinition>(entity.Data, JsonOpts);
                    if (quest is not null) registryService.Quests.Register(quest);
                    break;
                case "lore_entry":
                    var loreEntry = JsonSerializer.Deserialize<LoreEntry>(entity.Data, JsonOpts);
                    if (loreEntry is not null) registryService.LoreEntries.Register(loreEntry);
                    break;
                case "narrator_preset":
                    var preset = JsonSerializer.Deserialize<NarratorPreset>(entity.Data, JsonOpts);
                    if (preset is not null) registryService.NarratorPresets.Register(preset);
                    break;
                case "storyline":
                    var storyline = JsonSerializer.Deserialize<StorylineContext>(entity.Data, JsonOpts);
                    if (storyline is not null) registryService.Storylines.Register(storyline);
                    break;
                default:
                    _logger.LogWarning("Unknown content type in DB: {ContentType}", entity.ContentType);
                    break;
            }
        }

        registryService.LogRegistrySummary();
    }

    private static ContentRegistryEntity ToEntity<T>(string contentType, T entry) where T : IRegistryEntry => new()
    {
        ContentType = contentType,
        Id = entry.Id,
        Name = entry.Name,
        Data = JsonSerializer.Serialize(entry, typeof(T), JsonOpts)
    };

    private static T? DeserializeYaml<T>(string yaml)
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .WithEnumNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<T>(yaml);
    }

    private static ItemTemplate ToItemTemplate(LoreItemDto item)
    {
        var itemType = ParseItemType(item.Type);
        return new ItemTemplate
        {
            Id = item.Id ?? string.Empty,
            Name = item.Name ?? item.Id ?? string.Empty,
            Description = item.Description,
            Type = itemType,
            DamageDice = item.DamageDice,
            DamageStat = item.DamageStat,
            ArmorValue = item.ArmorValue,
            IsEquippable = item.IsEquippable ?? GAE.Core.Models.InventoryItem.IsEquippableType(itemType),
            IsConsumable = item.IsConsumable ?? false,
            IsTwoHanded = ResolveIsTwoHanded(item),
            Effect = item.Effect,
            Value = item.Value,
            StatBonuses = item.StatBonuses ?? new(),
            RequiredLevel = item.RequiredLevel,
            Rarity = string.IsNullOrWhiteSpace(item.Rarity) ? "common" : item.Rarity,
            Tags = item.Tags ?? []
        };
    }

    private static List<LoreItemDto> ExtractLoreItems(string loreSeedYaml)
    {
        var seed = DeserializeYaml<LoreSeedForItems>(loreSeedYaml);
        var items = new List<LoreItemDto>();

        var allRooms = new List<LoreRoomForItems>();
        if (seed?.StartingRoom is not null) allRooms.Add(seed.StartingRoom);
        if (seed?.Rooms is not null) allRooms.AddRange(seed.Rooms);

        foreach (var room in allRooms)
        {
            if (room.Items is not null) items.AddRange(room.Items);
            if (room.Npcs is null) continue;
            foreach (var npc in room.Npcs)
            {
                if (npc.ShopInventory is not null) items.AddRange(npc.ShopInventory);
                if (npc.Loot is not null) items.AddRange(npc.Loot);
            }
        }

        return items;
    }

    private static bool ResolveIsTwoHanded(LoreItemDto item)
    {
        return item.IsTwoHanded ?? item.Properties?.TwoHanded ?? false;
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
        "potion" or "healing draught" => ItemType.Potion,
        "scroll" => ItemType.Scroll,
        "key" => ItemType.Key,
        "quest" or "questitem" => ItemType.QuestItem,
        _ => ItemType.Misc
    };

    // ── YAML seed DTOs (mirrors RegistrySeedLoader's private DTOs) ──

    private class SpellSeedFile { public List<SpellDefinition>? Spells { get; set; } }
    private class ClassSeedFile { public List<ClassDefinition>? Classes { get; set; } }
    private class RaceSeedFile { public List<RaceDefinition>? Races { get; set; } }
    private class MonsterSeedFile { public List<MonsterTemplate>? Monsters { get; set; } }
    private class ItemSeedFile { public List<LoreItemDto>? Items { get; set; } }
    private class QuestSeedFile { public List<QuestDefinition>? Quests { get; set; } }
    private class LoreEntrySeedFile { public List<LoreEntry>? LoreEntries { get; set; } }
    private class NarratorPresetSeedFile { public List<NarratorPreset>? NarratorPresets { get; set; } }

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
        public LoreItemProperties? Properties { get; set; }
    }

    private class LoreItemProperties
    {
        public bool? TwoHanded { get; set; }
    }
}
