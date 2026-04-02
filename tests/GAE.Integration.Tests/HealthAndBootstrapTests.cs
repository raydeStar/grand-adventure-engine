using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GAE.Integration.Tests;

/// <summary>
/// Tests that the server boots, health endpoints respond, and core API routes work
/// through the full DI pipeline — no external services required.
/// </summary>
public class HealthAndBootstrapTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly HttpClient _anonymousClient;
    private readonly HttpClient _dashboardClient;

    public HealthAndBootstrapTests(GaeWebApplicationFactory factory)
    {
        _anonymousClient = factory.CreateClient();
        _dashboardClient = factory.CreateUserClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _anonymousClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthLive_ReturnsAlive()
    {
        var response = await _anonymousClient.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alive", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthReady_ReturnsReady()
    {
        var response = await _anonymousClient.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetRooms_ReturnsAtLeastSpawnRoom()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/rooms");
        response.EnsureSuccessStatusCode();

        var rooms = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(rooms.GetArrayLength() >= 1, "Expected at least the spawn room to be seeded");

        var spawnRoom = rooms.EnumerateArray().FirstOrDefault(r =>
            r.GetProperty("id").GetString() == "spawn");
        Assert.Equal("The Crossroads Inn", spawnRoom.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetRoom_Spawn_ReturnsRoom()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/rooms/spawn");
        response.EnsureSuccessStatusCode();

        var room = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("spawn", room.GetProperty("id").GetString());
        Assert.Equal("The Crossroads Inn", room.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetRoom_NonExistent_Returns404()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/rooms/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DashboardEndpoints_RequireAuthentication()
    {
        var response = await _anonymousClient.GetAsync("/api/dashboard/players");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetPlayers_InitiallyEmpty()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/players");
        response.EnsureSuccessStatusCode();

        var players = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, players.GetArrayLength());
    }

    [Fact]
    public async Task GetPlayer_NonExistent_Returns404()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/players/nobody");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStory_InitiallyEmpty()
    {
        var response = await _dashboardClient.GetAsync("/api/dashboard/story");
        response.EnsureSuccessStatusCode();

        var story = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, story.GetArrayLength());
    }
}
