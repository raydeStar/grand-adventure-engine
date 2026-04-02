using System.Net.Http.Json;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

/// <summary>
/// Full game flow: create a character via the engine, then exercise the dashboard
/// API to look, move, rest, check stats/inventory — all through HTTP endpoints.
/// </summary>
public class GameFlowTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public GameFlowTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    private async Task<PlayerCharacter> CreateTestCharacterAsync(string playerId = "test-player-1")
    {
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();

        var concept = new CharacterConcept
        {
            PlayerDiscordId = playerId,
            Name = "Testus the Bold",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray,
            Backstory = "A test warrior."
        };

        return await engine.CreateCharacterFromConceptAsync(concept);
    }

    [Fact]
    public async Task CreateCharacter_ThenGetPlayer_ReturnsCharacter()
    {
        var player = await CreateTestCharacterAsync("flow-player-1");

        var response = await _client.GetAsync($"/api/dashboard/players/{player.Id}");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Testus the Bold", body.GetProperty("name").GetString());
        Assert.Equal("Human", body.GetProperty("race").GetString());
        Assert.Equal("Fighter", body.GetProperty("class").GetString());
        Assert.Equal("spawn", body.GetProperty("currentRoomId").GetString());
        Assert.True(body.GetProperty("hp").GetInt32() > 0);
    }

    [Fact]
    public async Task PostAction_Look_ReturnsRoomDescription()
    {
        await CreateTestCharacterAsync("flow-player-look");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-look",
            Command = "look"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());

        var summary = result.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("The Crossroads Inn", summary);
        Assert.Contains("Exits:", summary);
    }

    [Fact]
    public async Task PostAction_Stats_ReturnsCharacterStats()
    {
        await CreateTestCharacterAsync("flow-player-stats");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-stats",
            Command = "stats"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());

        var summary = result.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Testus the Bold", summary);
        Assert.Contains("HP:", summary);
        Assert.Contains("STR:", summary);
    }

    [Fact]
    public async Task PostAction_Inventory_ReturnsInventory()
    {
        await CreateTestCharacterAsync("flow-player-inv");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-inv",
            Command = "inventory"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("Inventory", result.GetProperty("mechanicalSummary").GetString()!);
    }

    [Fact]
    public async Task PostAction_Help_ReturnsCommandList()
    {
        await CreateTestCharacterAsync("flow-player-help");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-help",
            Command = "help"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());

        var summary = result.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Available Commands", summary);
        Assert.Contains("go <direction>", summary);
    }

    [Fact]
    public async Task PostAction_MoveNorth_MovesToNewRoom()
    {
        await CreateTestCharacterAsync("flow-player-move");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-move",
            Command = "go north"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("north", result.GetProperty("mechanicalSummary").GetString()!);

        // Verify player moved
        var playerResponse = await _client.GetAsync("/api/dashboard/players/flow-player-move");
        var player = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("spawn", player.GetProperty("currentRoomId").GetString());
    }

    [Fact]
    public async Task PostAction_MoveInvalidDirection_Fails()
    {
        await CreateTestCharacterAsync("flow-player-bad-dir");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-bad-dir",
            Command = "go up"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("no exit", result.GetProperty("mechanicalSummary").GetString()!);
    }

    [Fact]
    public async Task PostAction_ShortRest_RecoversSomeHp()
    {
        await CreateTestCharacterAsync("flow-player-rest");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-rest",
            Command = "rest"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Contains("rest", result.GetProperty("mechanicalSummary").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.GetProperty("diceRolls").GetArrayLength() > 0, "Rest should produce dice rolls");
    }

    [Fact]
    public async Task PostAction_AttackNpc_ProducesDiceRolls()
    {
        await CreateTestCharacterAsync("flow-player-atk");

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-atk",
            Command = "attack mara"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Attack may succeed or miss, but should always have dice rolls
        Assert.True(result.GetProperty("diceRolls").GetArrayLength() > 0, "Attack should produce at least an attack roll");
    }

    [Fact]
    public async Task PostAction_UnknownPlayer_Fails()
    {
        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "nobody-exists",
            Command = "look"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("not found", result.GetProperty("mechanicalSummary").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoveAndLook_GeneratesNewRoom_ThenStoryRecorded()
    {
        await CreateTestCharacterAsync("flow-player-story");

        // Move to generate a new room
        await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-story",
            Command = "go north"
        });

        // Look around in the new room
        await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-story",
            Command = "look"
        });

        // Check that story entries were recorded
        var storyResponse = await _client.GetAsync("/api/dashboard/story?playerId=flow-player-story");
        storyResponse.EnsureSuccessStatusCode();

        var story = await storyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(story.GetArrayLength() >= 2, "Expected at least 2 story entries (move + look)");
    }

    [Fact]
    public async Task MultipleRoomDiscovery_ExpandsWorldMap()
    {
        await CreateTestCharacterAsync("flow-player-explore");

        // Move north
        await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = "flow-player-explore",
            Command = "go north"
        });

        // Check room count
        var roomsResponse = await _client.GetAsync("/api/dashboard/rooms");
        var rooms = await roomsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rooms.GetArrayLength() >= 2, "Expected spawn + at least 1 generated room");
    }
}
