using Discord.WebSocket;
using GAE.Core.Interfaces;
using GAE.Dashboard.Api.Hubs;
using GAE.Dashboard.Api.Security;
using GAE.Dashboard.Api.Services;
using GAE.Discord;
using GAE.Engine;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Narrator;
using GAE.WikiSync;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using Microsoft.AspNetCore.Authentication.Cookies;
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

// State management — single-writer model
var dataDir = builder.Environment.IsProduction() && Directory.Exists("/app/data")
    ? "/app/data"
    : Path.Combine(builder.Environment.ContentRootPath, "..", "..", "data");
Directory.CreateDirectory(dataDir);

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

// Engine
builder.Services.AddSingleton<IProbabilityEngine, ProbabilityEngine>();
builder.Services.AddSingleton<CommandParser>();
builder.Services.AddSingleton<IGameEngine, GameEngine>();

// Narrator — LM Studio HTTP client
var lmStudioEndpoint = builder.Configuration["LmStudio:Endpoint"] ?? "http://localhost:1234";
var lmStudioModel = builder.Configuration["LmStudio:Model"] ?? "default";
builder.Services.AddHttpClient<INarratorService, NarratorService>(client =>
{
    client.BaseAddress = new Uri(lmStudioEndpoint + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());

// Wiki.js — GraphQL client
var wikiUrl = builder.Configuration["WikiJs:Url"] ?? "http://localhost:3000";
var wikiApiKey = builder.Configuration["WikiJs:ApiKey"] ?? "";
builder.Services.AddSingleton<GraphQL.Client.Abstractions.IGraphQLClient>(sp =>
{
    var client = new GraphQLHttpClient(
        $"{wikiUrl}/graphql",
        new SystemTextJsonSerializer());
    if (!string.IsNullOrEmpty(wikiApiKey))
        client.HttpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", wikiApiKey);
    return client;
});
builder.Services.AddSingleton<IWikiService, WikiService>();

// Discord bot
var discordToken = builder.Configuration["Discord:Token"] ?? "";
builder.Services.AddSingleton(new DiscordSocketConfig { GatewayIntents = Discord.GatewayIntents.AllUnprivileged | Discord.GatewayIntents.MessageContent });
builder.Services.AddSingleton<DiscordSocketClient>();
if (!string.IsNullOrEmpty(discordToken) && discordToken != "YOUR_DISCORD_BOT_TOKEN_HERE")
{
    builder.Services.AddHostedService(sp => new DiscordBotService(
        sp.GetRequiredService<DiscordSocketClient>(),
        sp.GetRequiredService<IGameEngine>(),
        sp.GetRequiredService<IStateManager>(),
        sp.GetRequiredService<ILogger<DiscordBotService>>(),
        discordToken));
}

// SignalR + Controllers
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<IGameEventBroadcaster, SignalRGameEventBroadcaster>();
builder.Services.Configure<DashboardAuthOptions>(builder.Configuration.GetSection(DashboardAuthOptions.SectionName));
builder.Services.AddSingleton<IDashboardAuthService, DashboardAuthService>();
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

// State recovery: replay from checkpoint + journal
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

try
{
    var wiki = app.Services.GetRequiredService<IWikiService>();
    if (!await wiki.IsHealthyAsync())
        app.Logger.LogWarning("Wiki.js not healthy at {Url} — wiki sync disabled", wikiUrl);
}
catch
{
    app.Logger.LogWarning("Wiki.js unreachable at {Url} — wiki sync disabled", wikiUrl);
}

// Seed starting room if not already present
var stateManager = app.Services.GetRequiredService<IStateManager>();
var startRoom = await stateManager.GetRoomAsync("spawn");
if (startRoom is null)
{
    var lorePath = Path.Combine(configDir, "lore-seed.yaml");
    if (File.Exists(lorePath))
    {
        var loreYaml = await File.ReadAllTextAsync(lorePath);
        var lore = yamlDeserializer.Deserialize<LoreSeed>(loreYaml);

        if (lore?.StartingRoom is not null)
        {
            var room = new GAE.Core.Models.Room
            {
                Id = lore.StartingRoom.Id ?? "spawn",
                Name = lore.StartingRoom.Name ?? "The Crossroads Inn",
                Description = lore.StartingRoom.Description ?? "A weathered inn at a crossroads.",
                IsDiscovered = true,
                DiscoveredAt = DateTimeOffset.UtcNow
            };

            if (lore.StartingRoom.Exits is not null)
                foreach (var exit in lore.StartingRoom.Exits)
                    room.Exits[exit.Key] = exit.Value;

            if (lore.StartingRoom.Npcs is not null)
                room.Npcs.AddRange(lore.StartingRoom.Npcs.Select(n => new GAE.Core.Models.Npc
                {
                    Id = n.Id ?? Guid.NewGuid().ToString(),
                    Name = n.Name ?? "Unknown",
                    Personality = n.Personality ?? "",
                    Faction = n.Faction ?? "neutral"
                }));

            if (lore.StartingRoom.EnvironmentTags is not null)
                room.EnvironmentTags.AddRange(lore.StartingRoom.EnvironmentTags);

            await stateManager.SaveRoomAsync(room);
            app.Logger.LogInformation("Seeded starting room: {Room}", room.Name);
        }
    }
}

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

app.MapGet("/health/wiki", async (IWikiService wiki) =>
{
    try
    {
        var healthy = await wiki.IsHealthyAsync();
        return healthy
            ? Results.Ok(new { status = "healthy", service = "wiki.js" })
            : Results.Json(new { status = "degraded", service = "wiki.js", note = "Wiki sync unavailable — game continues without wiki" }, statusCode: 503);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "degraded", service = "wiki.js", error = ex.Message, note = "Wiki sync unavailable — game continues without wiki" }, statusCode: 503);
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

// Flush checkpoint on shutdown
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogInformation("Flushing state checkpoint on shutdown...");
    replayService.FlushAsync().GetAwaiter().GetResult();
});

app.Run();

// Lore seed DTOs for YAML deserialization
internal class LoreSeed
{
    public LoreWorld? World { get; set; }
    public List<LoreRegion>? Regions { get; set; }
    public LoreRoom? StartingRoom { get; set; }
    public List<LoreFaction>? Factions { get; set; }
}

internal class LoreWorld { public string? Name { get; set; } public string? Description { get; set; } }
internal class LoreRegion { public string? Id { get; set; } public string? Name { get; set; } public string? Description { get; set; } public List<string>? Tags { get; set; } }
internal class LoreRoom
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Exits { get; set; }
    public List<LoreNpc>? Npcs { get; set; }
    public List<string>? EnvironmentTags { get; set; }
}
internal class LoreNpc { public string? Id { get; set; } public string? Name { get; set; } public string? Personality { get; set; } public string? Faction { get; set; } }
internal class LoreFaction { public string? Id { get; set; } public string? Name { get; set; } public string? Description { get; set; } }

// Enables WebApplicationFactory<Program> in integration tests
public partial class Program { }
