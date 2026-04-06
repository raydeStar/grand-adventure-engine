using Discord.WebSocket;
using GAE.Core.Interfaces;
using GAE.Dashboard.Api.Hubs;
using GAE.Dashboard.Api.Security;
using GAE.Dashboard.Api.Services;
using GAE.Discord;
using GAE.Core.Registry;
using GAE.Engine;
using GAE.Engine.Configuration;
using GAE.Engine.Data;
using GAE.Engine.Registry;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using GAE.Narrator;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging in Production
if (builder.Environment.IsProduction())
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
    });
}

// Load external config (with environment-specific overrides)
var configDir = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "config");
if (builder.Environment.IsProduction() && Directory.Exists("/app/config"))
    configDir = "/app/config";

builder.Configuration
    .AddJsonFile(Path.Combine(configDir, "appsettings.json"), optional: true, reloadOnChange: true)
    .AddJsonFile(Path.Combine(configDir, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Load game rules from YAML
var rulesPath = Path.Combine(configDir, "game-rules.yaml");
var rulesYaml = File.Exists(rulesPath) ? await File.ReadAllTextAsync(rulesPath) : "";
var yamlDeserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();
var gameRules = !string.IsNullOrEmpty(rulesYaml)
    ? yamlDeserializer.Deserialize<GameRulesConfig>(rulesYaml)
    : new GameRulesConfig();
builder.Services.AddSingleton(gameRules);

// State management — feature-flagged: PostgreSQL (default) or file-based (fallback)
var dataDir = builder.Environment.IsProduction() && Directory.Exists("/app/data")
    ? "/app/data"
    : Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data");
Directory.CreateDirectory(dataDir);

var usePostgres = builder.Configuration.GetValue("Persistence:UsePostgres", true);

if (usePostgres)
{
    // PostgreSQL via EF Core — DbContext factory allows singleton state managers
    var connectionString = builder.Configuration.GetConnectionString("GameDatabase")
        ?? "Host=localhost;Port=5432;Database=gae;Username=gae_app;Password=gae_dev_password";
    builder.Services.AddDbContextFactory<GaeDbContext>(options =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsAssembly("GAE.Engine")));

    builder.Services.AddSingleton<IStateManager, EfCoreStateManager>();
    builder.Services.AddSingleton<IConversationLogger, EfCoreConversationLogger>();
    builder.Services.AddSingleton<IWorldRepository, EfCoreWorldRepository>();
    builder.Services.AddSingleton<ContentSeedService>();

    // Keep file-based services available for data migration
    builder.Services.AddSingleton<InMemoryStateManager>();
    builder.Services.AddSingleton<IStateJournal>(sp =>
        new FileStateJournal(
            Path.Combine(dataDir, "journal.jsonl"),
            sp.GetRequiredService<ILogger<FileStateJournal>>()));
    builder.Services.AddSingleton<IStateCheckpointStore>(sp =>
        new FileStateCheckpointStore(
            Path.Combine(dataDir, "checkpoints"),
            sp.GetRequiredService<ILogger<FileStateCheckpointStore>>()));
    builder.Services.AddSingleton<StateReplayService>();
    builder.Services.AddSingleton<IStateReplayService>(sp => sp.GetRequiredService<StateReplayService>());
    builder.Services.AddSingleton<DataMigrationService>();
}
else
{
    // Legacy file-based persistence — singleton in-memory + journaled
    builder.Services.AddSingleton<IConversationLogger>(sp =>
        new FileConversationLogger(
            Path.Combine(dataDir, "conversations.jsonl"),
            sp.GetRequiredService<ILogger<FileConversationLogger>>()));

    builder.Services.AddSingleton<InMemoryStateManager>();
    builder.Services.AddSingleton<IStateJournal>(sp =>
        new FileStateJournal(
            Path.Combine(dataDir, "journal.jsonl"),
            sp.GetRequiredService<ILogger<FileStateJournal>>()));
    builder.Services.AddSingleton<IStateCheckpointStore>(sp =>
        new FileStateCheckpointStore(
            Path.Combine(dataDir, "checkpoints"),
            sp.GetRequiredService<ILogger<FileStateCheckpointStore>>()));
    builder.Services.AddSingleton<StateReplayService>();
    builder.Services.AddSingleton<IStateReplayService>(sp => sp.GetRequiredService<StateReplayService>());
    builder.Services.AddSingleton<JournaledStateManager>();
    builder.Services.AddSingleton<IStateManager>(sp => sp.GetRequiredService<JournaledStateManager>());
    builder.Services.AddSingleton<IWorldRepository, InMemoryWorldRepository>();
}

builder.Services.AddSingleton<WorldBootstrapService>();

// World seeding config — used by admin reset endpoint
builder.Services.AddSingleton(new WorldSeedConfig
{
    LoreSeedPath = Path.Combine(configDir, "lore-seed.yaml"),
    YamlDeserializer = yamlDeserializer
});

// Engine
builder.Services.AddSingleton<IProbabilityEngine, ProbabilityEngine>();
builder.Services.AddSingleton<CommandParser>();

// Content Registries — loaded from YAML seeds, holds all game content definitions
builder.Services.AddSingleton<ContentRegistryService>();
builder.Services.AddSingleton<IContentRegistryService>(sp => sp.GetRequiredService<ContentRegistryService>());

builder.Services.AddSingleton<QuestEngine>();
builder.Services.AddSingleton<QuestTracker>();
// RealmTravelService registration deferred below INarratorService (depends on it)
builder.Services.AddSingleton<IGameEngine, GameEngine>();

// Narrator — LM Studio HTTP client
var lmStudioEndpoint = builder.Configuration["LmStudio:Endpoint"] ?? "http://localhost:1234";
var lmStudioModel = builder.Configuration["LmStudio:Model"] ?? "default";
builder.Services.AddHttpClient("LmStudio", client =>
{
    client.BaseAddress = new Uri(lmStudioEndpoint + "/");
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddSingleton<WorldKnowledgeBuilder>();
builder.Services.AddSingleton<INarratorService>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("LmStudio");
    var logger = sp.GetRequiredService<ILogger<NarratorService>>();
    var knowledge = sp.GetRequiredService<WorldKnowledgeBuilder>();
    var conversationLogger = sp.GetRequiredService<IConversationLogger>();
    var registry = sp.GetRequiredService<IContentRegistryService>();
    var worldContext = sp.GetService<IWorldContext>();
    var worldRepository = sp.GetService<IWorldRepository>();
    return new NarratorService(httpClient, logger, lmStudioModel, knowledge, conversationLogger, registry, worldContext, worldRepository);
});

builder.Services.AddSingleton<IRealmTravelService>(sp =>
{
    var stateManager = sp.GetRequiredService<IStateManager>();
    var worldRepository = sp.GetRequiredService<IWorldRepository>();
    var logger = sp.GetRequiredService<ILogger<RealmTravelService>>();
    var rules = sp.GetService<GameRulesConfig>();
    var narrator = sp.GetService<INarratorService>();
    return new RealmTravelService(stateManager, worldRepository, logger, rules, narrator);
});

// Discord bot
var discordToken = builder.Configuration["Discord:Token"] ?? "";
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = Discord.GatewayIntents.AllUnprivileged | Discord.GatewayIntents.MessageContent });
builder.Services.AddSingleton<DiscordSocketClient>();
if (!string.IsNullOrEmpty(discordToken) && discordToken != "YOUR_DISCORD_BOT_TOKEN_HERE")
{
    builder.Services.AddSingleton<DiscordBotService>(sp => new DiscordBotService(
        sp.GetRequiredService<DiscordSocketClient>(),
        sp.GetRequiredService<IGameEngine>(),
        sp.GetRequiredService<IStateManager>(),
        sp.GetRequiredService<INarratorService>(),
        sp.GetRequiredService<ILogger<DiscordBotService>>(),
        discordToken,
        dataDir));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscordBotService>());
    builder.Services.AddSingleton<IDiscordNotifier>(sp => sp.GetRequiredService<DiscordBotService>());
}

// SignalR + Controllers
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<IWorldContext, WorldContext>();
builder.Services.AddSingleton<IGameEventBroadcaster, SignalRGameEventBroadcaster>();
builder.Services.Configure<DashboardAuthOptions>(builder.Configuration.GetSection(DashboardAuthOptions.SectionName));
builder.Services.AddSingleton<IDashboardAuthService, DashboardAuthService>();
// Persist DataProtection keys so auth cookies survive container restarts
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "data-protection-keys")))
    .SetApplicationName("GAE-Dashboard");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "gae.dashboard.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.Events = DashboardSecurityExtensions.CreateCookieEvents();
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(DashboardPolicies.UserAccess, policy =>
        policy.RequireAuthenticatedUser().RequireRole(DashboardRoles.User, DashboardRoles.Admin));

    options.AddPolicy(DashboardPolicies.AdminAccess, policy =>
        policy.RequireAuthenticatedUser().RequireRole(DashboardRoles.Admin));
});

// CORS for dashboard client
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://localhost:3001", "http://localhost:8080")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddHttpClient();

var app = builder.Build();

var authService = app.Services.GetRequiredService<IDashboardAuthService>();
foreach (var warning in authService.GetStartupWarnings())
{
    app.Logger.LogWarning("{Warning}", warning);
}

// State recovery: mode-dependent startup
if (usePostgres)
{
    // Apply EF Core migrations automatically
    {
        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<GaeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database migrations applied");

        // One-time data migration from file-based state (if any exists and DB is empty)
        var migrator = app.Services.GetRequiredService<DataMigrationService>();
        await migrator.MigrateAsync();

        // Optionally import historical journal and conversation logs
        var journalPath = Path.Combine(dataDir, "journal.jsonl");
        if (File.Exists(journalPath) && !await db.GameEvents.AnyAsync())
            await migrator.ImportJournalHistoryAsync(journalPath);

        var conversationsPath = Path.Combine(dataDir, "conversations.jsonl");
        if (File.Exists(conversationsPath) && !await db.ConversationLogs.AnyAsync())
            await migrator.ImportConversationHistoryAsync(conversationsPath);
    }
}
else
{
    // Legacy file-based: replay from checkpoint + journal
    var replayService = app.Services.GetRequiredService<StateReplayService>();
    try
    {
        await replayService.ReplayAsync();
        app.Logger.LogInformation("State recovery complete");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "State recovery encountered issues — starting with fresh state");
    }
}

// Probe external services and log degraded mode warnings
try
{
    using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var lmResponse = await probeClient.GetAsync(lmStudioEndpoint + "/v1/models");
    if (!lmResponse.IsSuccessStatusCode)
        app.Logger.LogWarning("LM Studio not responding at {Endpoint} — narration will use fallback text", lmStudioEndpoint);
}
catch
{
    app.Logger.LogWarning("LM Studio unreachable at {Endpoint} — narration will use fallback text", lmStudioEndpoint);
}

// ── Load content registries ──────────────────────────────────────────
var registryService = app.Services.GetRequiredService<ContentRegistryService>();

if (usePostgres)
{
    // PostgreSQL: seed from YAML on first run, then always load from DB
    var contentSeed = app.Services.GetRequiredService<ContentSeedService>();
    await contentSeed.SeedAndLoadAsync(registryService, configDir);

    // Load GameRulesConfig from DB (seeding from YAML if DB is empty)
    var dbRules = await contentSeed.SeedAndLoadGameRulesAsync(configDir);
    gameRules.CharacterCreation = dbRules.CharacterCreation;
    gameRules.Stats = dbRules.Stats;
    gameRules.Combat = dbRules.Combat;
    gameRules.SkillChecks = dbRules.SkillChecks;
    gameRules.Rest = dbRules.Rest;
    gameRules.Death = dbRules.Death;
    gameRules.Loot = dbRules.Loot;
    gameRules.Leveling = dbRules.Leveling;
}
else
{
    // Legacy: load from YAML seed files directly
    var spellsPath = Path.Combine(configDir, "spells-seed.yaml");
    if (File.Exists(spellsPath))
        RegistrySeedLoader.LoadSpells(registryService.Spells, await File.ReadAllTextAsync(spellsPath), app.Logger);

    var classesPath = Path.Combine(configDir, "classes-seed.yaml");
    if (File.Exists(classesPath))
        RegistrySeedLoader.LoadClasses(registryService.Classes, await File.ReadAllTextAsync(classesPath), app.Logger);

    var racesPath = Path.Combine(configDir, "races-seed.yaml");
    if (File.Exists(racesPath))
        RegistrySeedLoader.LoadRaces(registryService.Races, await File.ReadAllTextAsync(racesPath), app.Logger);

    var loreSeedPath = Path.Combine(configDir, "lore-seed.yaml");
    if (File.Exists(loreSeedPath))
        RegistrySeedLoader.LoadItemsFromLoreSeed(registryService.Items, await File.ReadAllTextAsync(loreSeedPath), app.Logger);

    var dungeonItemsPath = Path.Combine(configDir, "dungeon-items.yaml");
    if (File.Exists(dungeonItemsPath))
        RegistrySeedLoader.LoadItems(registryService.Items, await File.ReadAllTextAsync(dungeonItemsPath), app.Logger);

    var monstersPath = Path.Combine(configDir, "monsters.yaml");
    if (File.Exists(monstersPath))
        RegistrySeedLoader.LoadMonsters(registryService.Monsters, await File.ReadAllTextAsync(monstersPath), app.Logger);

    var questsPath = Path.Combine(configDir, "quests.yaml");
    if (File.Exists(questsPath))
        RegistrySeedLoader.LoadQuests(registryService.Quests, registryService.Items, await File.ReadAllTextAsync(questsPath), app.Logger);

    registryService.LogRegistrySummary();
}

// Seed world from lore-seed.yaml if spawn room is not already present
using (var seedScope = app.Services.CreateScope())
{
    var stateManager = seedScope.ServiceProvider.GetRequiredService<IStateManager>();
    var startRoom = await stateManager.GetRoomAsync("spawn");
    if (startRoom is null)
    {
        var lorePath = Path.Combine(configDir, "lore-seed.yaml");
        if (File.Exists(lorePath))
        {
            var loreYaml = await File.ReadAllTextAsync(lorePath);
            var lore = yamlDeserializer.Deserialize<LoreSeed>(loreYaml);
            var seeded = await SeedWorldFromLore(lore, stateManager);
            app.Logger.LogInformation("Seeded {Count} rooms from lore-seed.yaml", seeded);
        }
    }
} // end seedScope

var worldBootstrap = app.Services.GetRequiredService<WorldBootstrapService>();
await worldBootstrap.EnsureDefaultWorldAsync(gameRules);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store";
    }
});
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<WorldContextMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHub<GameHub>("/hubs/game");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

// Liveness: is the process alive? (for orchestrator restarts)
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

// Readiness: can the service handle requests? (state recovered, core subsystems available)
app.MapGet("/health/ready", async (IStateManager sm) =>
{
    try
    {
        // Verify state manager is responsive
        await sm.GetRoomAsync("spawn");
        return Results.Ok(new { status = "ready", timestamp = DateTimeOffset.UtcNow });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "not-ready", error = ex.Message }, statusCode: 503);
    }
});

IResult? narratorHealthCache = null;
DateTimeOffset narratorHealthCacheExpiry = default;

app.MapGet("/health/narrator", async (HttpClient httpClient) =>
{
    if (narratorHealthCache is not null && DateTimeOffset.UtcNow < narratorHealthCacheExpiry)
        return narratorHealthCache;

    try
    {
        var response = await httpClient.GetAsync(lmStudioEndpoint + "/v1/models");
        narratorHealthCache = response.IsSuccessStatusCode
            ? Results.Ok(new { status = "healthy", service = "lm-studio" })
            : Results.Json(new { status = "degraded", service = "lm-studio", note = "Narration will use fallback text" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        narratorHealthCache = Results.Json(new { status = "degraded", service = "lm-studio", error = ex.Message, note = "Narration will use fallback text" }, statusCode: 503);
    }

    narratorHealthCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(60);
    return narratorHealthCache;
});

// Flush checkpoint on shutdown (only for file-based persistence)
if (!usePostgres)
{
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var replayService = app.Services.GetRequiredService<StateReplayService>();
    lifetime.ApplicationStopping.Register(() =>
    {
        app.Logger.LogInformation("Flushing state checkpoint on shutdown...");
        replayService.FlushAsync().GetAwaiter().GetResult();
    });
}

app.Run();

// ── World seeding helper ──────────────────────────────────────────────

static async Task<int> SeedWorldFromLore(LoreSeed? lore, IStateManager stateManager)
{
    if (lore is null) return 0;

    var allLoreRooms = new List<LoreRoom>();
    if (lore.StartingRoom is not null)
        allLoreRooms.Add(lore.StartingRoom);
    if (lore.Rooms is not null)
        allLoreRooms.AddRange(lore.Rooms);

    var count = 0;
    foreach (var loreRoom in allLoreRooms)
    {
        var room = ConvertLoreRoom(loreRoom);
        await stateManager.SaveRoomAsync(room);
        count++;
    }

    return count;
}

static GAE.Core.Models.Room ConvertLoreRoom(LoreRoom lr)
{
    var room = new GAE.Core.Models.Room
    {
        Id = lr.Id ?? Guid.NewGuid().ToString("N"),
        Name = lr.Name ?? "Unknown Room",
        Description = lr.Description ?? "An unremarkable room.",
        IsDiscovered = true,
        DiscoveredAt = DateTimeOffset.UtcNow
    };

    if (lr.Exits is not null)
        foreach (var exit in lr.Exits)
            room.Exits[exit.Key] = exit.Value;

    if (lr.Npcs is not null)
        room.Npcs.AddRange(lr.Npcs.Select(ConvertLoreNpc));

    if (lr.Items is not null)
        room.Items.AddRange(lr.Items.Select(ConvertLoreItem));

    if (lr.EnvironmentTags is not null)
        room.EnvironmentTags.AddRange(lr.EnvironmentTags);

    return room;
}

static GAE.Core.Models.Npc ConvertLoreNpc(LoreNpc n)
{
    var npc = new GAE.Core.Models.Npc
    {
        Id = n.Id ?? Guid.NewGuid().ToString("N"),
        Name = n.Name ?? "Unknown",
        Personality = n.Personality ?? "",
        Faction = n.Faction ?? "neutral",
        IsHostile = n.IsHostile,
        IsShopkeeper = n.IsShopkeeper,
        Level = n.Level ?? 1,
        Hp = n.Hp,
        MaxHp = n.MaxHp,
        AttackBonus = n.AttackBonus,
        DamageDice = n.DamageDice,
        Defense = n.Defense
    };

    if (n.KnowledgeScopes is not null)
        npc.KnowledgeScopes.AddRange(n.KnowledgeScopes);

    if (n.QuestsOffered is not null)
        npc.QuestsOffered.AddRange(n.QuestsOffered);

    if (n.Loot is not null)
        npc.LootTable.AddRange(n.Loot.Select(ConvertLoreItem));

    if (n.ShopInventory is not null)
        npc.ShopInventory.AddRange(n.ShopInventory.Select(ConvertLoreItem));

    return npc;
}

static GAE.Core.Models.InventoryItem ConvertLoreItem(LoreItem li)
{
    var itemType = Enum.TryParse<GAE.Core.Models.ItemType>(li.Type, true, out var parsed)
        ? parsed
        : GAE.Core.Models.ItemType.Misc;

    return new GAE.Core.Models.InventoryItem
    {
        Id = li.Id ?? Guid.NewGuid().ToString("N"),
        Name = li.Name ?? "Unknown Item",
        Description = li.Description ?? "",
        Type = itemType,
        Quantity = Math.Max(1, li.Quantity),
        Value = Math.Max(0, li.Value),
        DamageDice = li.DamageDice,
        DamageStat = li.DamageStat,
        ArmorValue = Math.Max(0, li.ArmorValue),
        IsEquippable = li.IsEquippable ?? GAE.Core.Models.InventoryItem.IsEquippableType(itemType),
        IsConsumable = li.IsConsumable ?? itemType is GAE.Core.Models.ItemType.Potion or GAE.Core.Models.ItemType.Scroll,
        IsTwoHanded = li.IsTwoHanded,
        Effect = li.Effect,
        StatBonuses = li.StatBonuses ?? new()
    };
}

// World seed config — allows admin endpoints to re-seed the world
public class WorldSeedConfig
{
    public string LoreSeedPath { get; set; } = string.Empty;
    public IDeserializer YamlDeserializer { get; set; } = null!;
}

// Lore seed DTOs for YAML deserialization
public class LoreSeed
{
    public LoreWorld? World { get; set; }
    public List<LoreRegion>? Regions { get; set; }
    public LoreRoom? StartingRoom { get; set; }
    public List<LoreRoom>? Rooms { get; set; }
    public List<LoreFaction>? Factions { get; set; }
}

public class LoreWorld { public string? Name { get; set; } public string? Description { get; set; } }
public class LoreRegion { public string? Id { get; set; } public string? Name { get; set; } public string? Description { get; set; } public List<string>? Tags { get; set; } }
public class LoreRoom
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Exits { get; set; }
    public List<LoreNpc>? Npcs { get; set; }
    public List<LoreItem>? Items { get; set; }
    public List<string>? EnvironmentTags { get; set; }
}
public class LoreNpc
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Personality { get; set; }
    public string? Faction { get; set; }
    public List<string>? KnowledgeScopes { get; set; }
    public bool IsHostile { get; set; }
    public int? Level { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? AttackBonus { get; set; }
    public string? DamageDice { get; set; }
    public int? Defense { get; set; }
    public List<LoreItem>? Loot { get; set; }
    public bool IsShopkeeper { get; set; }
    public List<LoreItem>? ShopInventory { get; set; }
    public List<string>? QuestsOffered { get; set; }
}
public class LoreItem
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }
    public int Quantity { get; set; } = 1;
    public int Value { get; set; }
    public string? DamageDice { get; set; }
    public string? DamageStat { get; set; }
    public int ArmorValue { get; set; }
    public bool? IsEquippable { get; set; }
    public bool? IsConsumable { get; set; }
    public bool IsTwoHanded { get; set; }
    public string? Effect { get; set; }
    public Dictionary<string, int>? StatBonuses { get; set; }
}
public class LoreFaction { public string? Id { get; set; } public string? Name { get; set; } public string? Description { get; set; } }

// Enables WebApplicationFactory<Program> in integration tests
public partial class Program { }
