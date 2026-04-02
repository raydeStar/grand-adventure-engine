using System.Net.Http.Json;
using GAE.Core.Models;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Narrator.Tests;

/// <summary>
/// Tests that verify real LM Studio connectivity.
/// Skipped automatically when LM Studio is not running.
/// Run explicitly with: dotnet test --filter "Category=LmStudio"
/// </summary>
[Trait("Category", "LmStudio")]
public class LmStudioConnectivityTests : IAsyncLifetime
{
    private const string LmStudioEndpoint = "http://localhost:1234";
    private HttpClient _httpClient = null!;
    private NarratorService _narrator = null!;
    private bool _lmStudioAvailable;

    public async Task InitializeAsync()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(LmStudioEndpoint + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        try
        {
            var response = await _httpClient.GetAsync("v1/models");
            _lmStudioAvailable = response.IsSuccessStatusCode;
        }
        catch
        {
            _lmStudioAvailable = false;
        }

        _narrator = new NarratorService(_httpClient, NullLogger<NarratorService>.Instance);
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task LmStudio_ModelsEndpoint_ReturnsModels()
    {
        Skip.IfNot(_lmStudioAvailable, "LM Studio not running at " + LmStudioEndpoint);

        var response = await _httpClient.GetAsync("v1/models");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ModelsResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Data);
    }

    [SkippableFact]
    public async Task LmStudio_ChatCompletion_ReturnsContent()
    {
        Skip.IfNot(_lmStudioAvailable, "LM Studio not running at " + LmStudioEndpoint);

        // Query the first available model instead of hardcoding "default"
        var modelsResponse = await _httpClient.GetFromJsonAsync<ModelsResponse>("v1/models");
        var modelId = modelsResponse?.Data?.FirstOrDefault()?.Id ?? "default";

        var request = new
        {
            model = modelId,
            messages = new[]
            {
                new { role = "system", content = "Respond with exactly one word." },
                new { role = "user", content = "Say hello." }
            },
            temperature = 0.1,
            max_tokens = 50
        };

        var response = await _httpClient.PostAsJsonAsync("v1/chat/completions", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<CompletionResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Choices);
        Assert.False(string.IsNullOrWhiteSpace(body.Choices[0].Message.Content));
    }

    [SkippableFact]
    public async Task NarratorService_NarrateAction_ProducesRealNarration()
    {
        Skip.IfNot(_lmStudioAvailable, "LM Studio not running at " + LmStudioEndpoint);

        var context = new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior", Level = 1 },
            CurrentRoom = new Room { Id = "town_square", Name = "Town Square", Description = "A bustling market square." },
            Action = new GameAction { PlayerId = "p1", RawInput = "look", Type = ActionType.Look },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You observe the town square." }
        };

        var narration = await _narrator.NarrateActionAsync(context);

        Assert.NotNull(narration);
        Assert.DoesNotContain("narrator clears his throat", narration, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task NarratorService_GenerateRoom_ProducesValidRoom()
    {
        Skip.IfNot(_lmStudioAvailable, "LM Studio not running at " + LmStudioEndpoint);

        var sourceRoom = new Room
        {
            Id = "start",
            Name = "Dark Forest Clearing",
            Description = "A mossy clearing in an ancient forest.",
            EnvironmentTags = ["forest", "dark", "ancient"]
        };

        var room = await _narrator.GenerateRoomAsync("forest_path", "north", sourceRoom);

        Assert.Equal("forest_path", room.Id);
        Assert.NotNull(room.Name);
        Assert.Contains("south", room.Exits.Keys);
    }

    // DTOs for raw API assertions
    private record ModelsResponse(ModelEntry[] Data);
    private record ModelEntry(string Id);
    private record CompletionResponse(CompletionChoice[] Choices);
    private record CompletionChoice(CompletionMessage Message);
    private record CompletionMessage(string Content);
}
