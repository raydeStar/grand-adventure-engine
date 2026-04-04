using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GAE.Integration.Tests;

public class AdminConsoleTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly HttpClient _anonymousClient;
    private readonly HttpClient _userClient;
    private readonly HttpClient _adminClient;

    public AdminConsoleTests(GaeWebApplicationFactory factory)
    {
        _anonymousClient = factory.CreateClient();
        _userClient = factory.CreateUserClient();
        _adminClient = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Root_ReturnsDashboardMarkup()
    {
        var response = await _anonymousClient.GetAsync("/");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Grand Adventure Engine", html);
        Assert.Contains("Admin Console", html);
        Assert.Contains("User Workspace", html);
    }

    [Fact]
    public async Task SessionEndpoint_WhenAnonymous_ReturnsOkNull()
    {
        var response = await _anonymousClient.GetAsync("/api/dashboard/auth/session");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("null", body.Trim());
    }

    [Fact]
    public async Task DashboardHealth_ReturnsAggregatedStatusesWithOkResponse()
    {
        var response = await _userClient.GetAsync("/api/dashboard/health");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("health").GetProperty("ok").GetBoolean());
        Assert.True(payload.TryGetProperty("health/wiki", out _));
        Assert.True(payload.TryGetProperty("health/narrator", out _));
    }

    [Fact]
    public async Task CreateCharacterEndpoint_CreatesCharacterWithRequestedId()
    {
        var response = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "dashboard-create-1",
            name = "Lyra of Tests",
            race = "Elf",
            @class = "Mage",
            statMethod = "StandardArray",
            backstory = "Provisioned through the dashboard API."
        });
        response.EnsureSuccessStatusCode();

        var player = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dashboard-create-1", player.GetProperty("id").GetString());
        Assert.Equal("Lyra of Tests", player.GetProperty("name").GetString());
        Assert.Equal("spawn", player.GetProperty("currentRoomId").GetString());
    }

    [Fact]
    public async Task CreateCharacterEndpoint_MissingName_ReturnsBadRequest()
    {
        var response = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "bad-request-player",
            name = "",
            race = "Human",
            @class = "Warrior",
            statMethod = "StandardArray"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdminSeedDemo_ReturnsDemoUserAndAdmin()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/seed-demo", new
        {
            replaceExisting = false
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("players").EnumerateArray().Select(player => player.GetProperty("id").GetString()).ToArray();

        Assert.Contains("demo-user", ids);
        Assert.Contains("demo-admin", ids);
    }

    [Fact]
    public async Task AdminSummary_ReturnsCountsAndCollections()
    {
        await _adminClient.PostAsJsonAsync("/api/dashboard/admin/seed-demo", new { replaceExisting = false });

        var response = await _adminClient.GetAsync("/api/dashboard/admin/summary");
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(summary.GetProperty("playerCount").GetInt32() >= 2);
        Assert.True(summary.GetProperty("roomCount").GetInt32() >= 1);
        Assert.True(summary.GetProperty("players").GetArrayLength() >= 2);
        Assert.True(summary.GetProperty("rooms").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task UserRole_CannotAccessAdminSummary()
    {
        var response = await _userClient.GetAsync("/api/dashboard/admin/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminMutations_UpdatePlayerState()
    {
        await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "mutation-player-1",
            name = "Mutation Target",
            race = "Human",
            @class = "Warrior",
            statMethod = "StandardArray"
        });

        var resourceResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/resources", new
        {
            playerId = "mutation-player-1",
            setGold = 25,
            setXp = 30,
            hpDelta = -2
        });
        resourceResponse.EnsureSuccessStatusCode();

        var itemResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/grant-item", new
        {
            playerId = "mutation-player-1",
            name = "Debug Blade",
            type = "Weapon",
            autoEquip = true
        });
        itemResponse.EnsureSuccessStatusCode();

        var statusResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/status", new
        {
            playerId = "mutation-player-1",
            name = "Inspired",
            type = "Buff",
            remainingTurns = 4,
            statModifiersText = "str:2"
        });
        statusResponse.EnsureSuccessStatusCode();

        var playerResponse = await _userClient.GetAsync("/api/dashboard/players/mutation-player-1");
        playerResponse.EnsureSuccessStatusCode();
        var player = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(25, player.GetProperty("gold").GetInt32());
        Assert.Equal(30, player.GetProperty("xp").GetInt32());
        // Debug Blade replaces Iron Sword in main hand (weapons always go to main hand)
        Assert.Equal("Debug Blade", player.GetProperty("equipment").GetProperty("mainHand").GetProperty("name").GetString());
        Assert.Contains(player.GetProperty("statusEffects").EnumerateArray(), effect => effect.GetProperty("name").GetString() == "Inspired");
    }

    [Fact]
    public async Task AdminMutations_UpdateWorldState()
    {
        await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "mutation-player-2",
            name = "World Walker",
            race = "Elf",
            @class = "Ranger",
            statMethod = "StandardArray"
        });

        var roomResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/room-fixture", new
        {
            roomId = "qa-lab",
            name = "QA Lab",
            description = "Fixture room for admin tests.",
            environmentTags = new[] { "qa", "boss" },
            items = new[] { new { name = "Inspection Token", type = "Misc", quantity = 1 } },
            npcs = new[] { new { name = "Sentinel", isHostile = true } }
        });
        roomResponse.EnsureSuccessStatusCode();

        var teleportResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/teleport", new
        {
            playerId = "mutation-player-2",
            roomId = "qa-lab",
            connectFromCurrentRoom = true,
            entryDirection = "north"
        });
        teleportResponse.EnsureSuccessStatusCode();

        var playerResponse = await _userClient.GetAsync("/api/dashboard/players/mutation-player-2");
        playerResponse.EnsureSuccessStatusCode();
        var player = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("qa-lab", player.GetProperty("currentRoomId").GetString());

        var roomReadResponse = await _userClient.GetAsync("/api/dashboard/rooms/qa-lab");
        roomReadResponse.EnsureSuccessStatusCode();
        var room = await roomReadResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("QA Lab", room.GetProperty("name").GetString());
        Assert.Contains(room.GetProperty("environmentTags").EnumerateArray(), tag => tag.GetString() == "qa");
        Assert.Contains(room.GetProperty("items").EnumerateArray(), item => item.GetProperty("name").GetString() == "Inspection Token");
        Assert.Contains(room.GetProperty("npcs").EnumerateArray(), npc => npc.GetProperty("name").GetString() == "Sentinel");
    }
}