using System.Net.Http.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.State;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GAE.Integration.Tests;

/// <summary>
/// Spins up the real ASP.NET host in-process but replaces external services
/// (Discord, Wiki.js, LM Studio narrator) with safe test stubs.
/// Each factory instance gets its own isolated data directory to avoid file contention.
/// </summary>
public class GaeWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string DefaultUserUsername = "user";
    public const string DefaultUserPassword = "GAE-User-Local!123";
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "GAE-Admin-Local!123";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "gae-tests", Guid.NewGuid().ToString("N"));

    public HttpClient CreateUserClient() => CreateAuthenticatedClient(DefaultUserUsername, DefaultUserPassword);

    public HttpClient CreateAdminClient() => CreateAuthenticatedClient(DefaultAdminUsername, DefaultAdminPassword);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Ensure tests use file-based persistence, not PostgreSQL
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:UsePostgres"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace narrator with a stub that always succeeds (no LM Studio needed)
            services.RemoveAll<INarratorService>();
            services.AddSingleton<INarratorService, StubNarratorService>();

            // Replace wiki with a stub (no Wiki.js container needed)
            services.RemoveAll<IWikiService>();
            services.AddSingleton<IWikiService, StubWikiService>();

            // Replace event broadcaster with no-op (no SignalR clients in tests)
            services.RemoveAll<IGameEventBroadcaster>();
            services.AddSingleton<IGameEventBroadcaster, StubGameEventBroadcaster>();

            // Replace file-based journal/checkpoint with isolated temp directory
            Directory.CreateDirectory(_dataDir);

            services.RemoveAll<IStateJournal>();
            services.AddSingleton<IStateJournal>(sp =>
                new FileStateJournal(
                    Path.Combine(_dataDir, "journal.jsonl"),
                    sp.GetRequiredService<ILogger<FileStateJournal>>()));

            services.RemoveAll<IStateCheckpointStore>();
            services.AddSingleton<IStateCheckpointStore>(sp =>
                new FileStateCheckpointStore(
                    Path.Combine(_dataDir, "checkpoints"),
                    sp.GetRequiredService<ILogger<FileStateCheckpointStore>>()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_dataDir, true); } catch { /* best effort */ }
        }
    }

    private HttpClient CreateAuthenticatedClient(string username, string password)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var response = client.PostAsJsonAsync("/api/dashboard/auth/login", new
        {
            username,
            password
        }).GetAwaiter().GetResult();

        response.EnsureSuccessStatusCode();
        return client;
    }
}

/// <summary>Narrator stub — returns deterministic text without calling LM Studio.</summary>
public class StubNarratorService : INarratorService
{
    public Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
        => Task.FromResult($"[Narration] {context.MechanicalResult.MechanicalSummary}");

    public Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
        => Task.FromResult(new Room
        {
            Id = roomId,
            Name = $"Generated Room ({roomId})",
            Description = $"A room generated to the {direction} of {sourceRoom.Name}.",
            Exits = new Dictionary<string, string> { [OppositeDir(direction)] = sourceRoom.Id }
        });

    public Task<Room> GenerateDungeonEntranceAsync(string dungeonId, int playerLevel, Room sourceRoom, CancellationToken ct = default)
        => Task.FromResult(new Room
        {
            Id = dungeonId,
            Name = $"Test Dungeon ({dungeonId})",
            Description = "A test dungeon entrance.",
            EnvironmentTags = ["dungeon", "generated_dungeon"],
            Exits = new Dictionary<string, string> { ["back"] = sourceRoom.Id }
        });

    public Task<Npc> GenerateNpcAsync(Room room, string? hint = null, CancellationToken ct = default)
        => Task.FromResult(new Npc { Id = Guid.NewGuid().ToString(), Name = "Test NPC", Personality = "Friendly" });

    public Task<string> GenerateAsciiArtAsync(string subject, CancellationToken ct = default)
        => Task.FromResult("[ASCII ART]");

    public Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default)
        => Task.FromResult($"{concept.Name} grew up in a test environment.");

    public Task<string?> ParseIntentAsync(string rawInput, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<FreeFormResponse> ProcessFreeFormAsync(PlayerCharacter player, Room room, string rawInput, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
        => Task.FromResult(new FreeFormResponse
        {
            Success = true,
            Narration = $"[FreeForm] {rawInput}"
        });

    public Task<FreeFormResponse> ProcessConversationTurnAsync(PlayerCharacter player, Room room, Npc npc, InteractionState interaction, string rawInput, CancellationToken ct = default)
        => Task.FromResult(new FreeFormResponse
        {
            Success = true,
            Narration = $"[Conversation] {npc.Name}: responding to '{rawInput}'"
        });

    public Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default)
        => Task.FromResult(new FreeFormResponse
        {
            Success = true,
            Narration = $"[Combat] vs {enemy.Name}: {rawInput}",
            InteractionUpdate = new InteractionUpdate
            {
                Mode = InteractionMode.Combat,
                CombatStatus = "ongoing",
                EnemyUpdate = new Dictionary<string, int> { ["hp"] = -3 }
            }
        });


    public Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(string playerDescription, string? previousSheet, CancellationToken ct = default)
        => Task.FromResult<CharacterCreationAiResponse?>(null);

    public Task<GAE.Core.Registry.ImprovisedSpellResult> EvaluateImprovisedSpellAsync(
        PlayerCharacter player, Room room, string spellName, string? target,
        int powerCap, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
        => Task.FromResult(new GAE.Core.Registry.ImprovisedSpellResult
        {
            PowerLevel = powerCap + 1,
            PlayerCap = powerCap,
            Success = false,
            ManaCost = 1,
            Narration = $"[Fizzle] {spellName}"
        });

    public Task<string> GenerateContentAsync(string contentType, string description, string? existingJson, CancellationToken ct = default)
        => Task.FromResult("{}");

    public string GetActiveModel() => "stub-model";
    public void SetActiveModel(string model) { }
    public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(["stub-model"]);

    public Task<SpellVetResponse?> VetSpellAsync(PlayerCharacter player, string spellDescription, Room room, CancellationToken ct = default)
        => Task.FromResult<SpellVetResponse?>(new SpellVetResponse
        {
            Approved = true,
            SpellName = spellDescription,
            Description = "A test spell.",
            Category = "damage",
            TargetType = "enemy",
            BasePower = 3,
            MpCost = 6,
            Narration = "[Spell] Learning new spell"
        });

    public Task<StatTranslationResponse?> TranslateStatsAsync(StatTranslationRequest request, CancellationToken ct = default)
        => Task.FromResult<StatTranslationResponse?>(null);

    public Task<string?> NarrateRealmTransitionAsync(string characterName, string fromWorld, string toWorld, string? translationNotes = null, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    private static string OppositeDir(string dir) => dir switch
    {
        "north" => "south", "south" => "north",
        "east" => "west", "west" => "east",
        "up" => "down", "down" => "up",
        _ => "back"
    };
}

/// <summary>Wiki stub — records calls without hitting Wiki.js.</summary>
public class StubWikiService : IWikiService
{
    public List<(string Path, string Title, string Content)> CreatedPages { get; } = [];

    public Task<bool> CreateOrUpdatePageAsync(string path, string title, string content, CancellationToken ct = default)
    {
        CreatedPages.Add((path, title, content));
        return Task.FromResult(true);
    }

    public Task<string?> GetPageContentAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<bool> PageExistsAsync(string path, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task SyncPlayerPageAsync(PlayerCharacter player, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SyncRoomPageAsync(Room room, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SyncNpcPageAsync(Npc npc, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SyncStoryEntryAsync(StoryEntry entry, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IReadOnlyList<WikiSearchResult>> SearchAsync(string query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WikiSearchResult>>([]);

    public Task<IReadOnlyList<WikiPageSummary>> GetPagesAsync(string pathPrefix = "", CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WikiPageSummary>>([]);
}

/// <summary>Event broadcaster stub — no-op for tests.</summary>
public class StubGameEventBroadcaster : IGameEventBroadcaster
{
    public List<GameEvent> BroadcastedEvents { get; } = [];

    public Task BroadcastEventAsync(GameEvent gameEvent, CancellationToken ct = default)
    {
        BroadcastedEvents.Add(gameEvent);
        return Task.CompletedTask;
    }

    public Task BroadcastActionResultAsync(ActionResult result, string playerId, CancellationToken ct = default)
        => Task.CompletedTask;
}
