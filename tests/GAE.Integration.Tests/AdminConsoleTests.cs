using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

public class AdminConsoleTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;
    private readonly HttpClient _anonymousClient;
    private readonly HttpClient _userClient;
    private readonly HttpClient _adminClient;

    public AdminConsoleTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
        _anonymousClient = factory.CreateClient();
        _userClient = factory.CreateUserClient();
        _adminClient = factory.CreateAdminClient();
    }

    private async Task<HttpResponseMessage> PostCoDmAsync(string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-GAE-Request", "co-dm");
        return await _adminClient.SendAsync(request);
    }

    [Fact]
    public async Task Root_ReturnsDashboardMarkup()
    {
        var response = await _anonymousClient.GetAsync("/");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Grand Adventure Engine", html);
        Assert.Contains("Admin Console", html);
        Assert.Contains("User Flow", html);
        Assert.Contains("tools=(self)", response.Headers.GetValues("Permissions-Policy").Single());
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

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Endpoint may return { player, heroIntro } wrapper or flat player object
        var player = result.TryGetProperty("player", out var p) ? p : result;
        Assert.Equal("dashboard-create-1", player.GetProperty("id").GetString());
        Assert.Equal("Lyra of Tests", player.GetProperty("name").GetString());
        Assert.Equal("spawn", player.GetProperty("currentRoomId").GetString());
    }

    [Fact]
    public async Task CreationOptions_ReturnsActiveWorldsAndBlindStorylines()
    {
        var response = await _userClient.GetAsync("/api/dashboard/creation-options");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("default-world", payload.GetProperty("defaultWorldId").GetString());
        Assert.True(payload.GetProperty("worlds").GetArrayLength() >= 1);
        Assert.True(payload.GetProperty("blindStorylines").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task CreateCharacterEndpoint_SelectedWorld_AssignsWorldIdentity()
    {
        var createWorldResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/worlds", new
        {
            id = "moon-realm",
            name = "Moon Realm",
            description = "A second launch-world option.",
            spawnRoomId = "spawn",
            isActive = true
        });
        createWorldResponse.EnsureSuccessStatusCode();

        var response = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "dashboard-create-world-1",
            name = "Selene of Tests",
            race = "Human",
            @class = "Cleric",
            worldId = "moon-realm",
            statMethod = "StandardArray",
            backstory = "Spawn me somewhere moonlit."
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var player = result.TryGetProperty("player", out var p) ? p : result;
        Assert.Equal("moon-realm", player.GetProperty("activeWorldId").GetString());
        Assert.Equal("moon-realm", player.GetProperty("homeWorldId").GetString());
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
        await _adminClient.PostAsJsonAsync("/api/dashboard/admin/seed-demo", new { replaceExisting = true });
        var message = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/send-message", new
        {
            playerId = "demo-user",
            message = "Temporary QA history that must not survive a deterministic reseed."
        });
        message.EnsureSuccessStatusCode();

        var response = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/seed-demo", new
        {
            replaceExisting = true
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("players").EnumerateArray().Select(player => player.GetProperty("id").GetString()).ToArray();

        Assert.Contains("demo-user", ids);
        Assert.Contains("demo-admin", ids);

        // Demo personas are handed to the shared user account so the documented Player Flow can resume them.
        var asUser = await _userClient.GetAsync("/api/dashboard/players/demo-user");
        Assert.Equal(HttpStatusCode.OK, asUser.StatusCode);
        var demoUser = await asUser.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GaeWebApplicationFactory.DefaultUserUsername, demoUser.GetProperty("ownerId").GetString());

        var adminPersonaAsUser = await _userClient.GetAsync("/api/dashboard/players/demo-admin");
        Assert.Equal(HttpStatusCode.NotFound, adminPersonaAsUser.StatusCode);
        var adminPersona = await _adminClient.GetFromJsonAsync<JsonElement>("/api/dashboard/players/demo-admin");
        Assert.Equal(GaeWebApplicationFactory.DefaultAdminUsername, adminPersona.GetProperty("ownerId").GetString());

        var cleanStory = await _userClient.GetFromJsonAsync<JsonElement>("/api/dashboard/story?playerId=demo-user");
        Assert.DoesNotContain(cleanStory.EnumerateArray(), entry =>
            entry.GetProperty("narration").GetString()?.Contains("Temporary QA history", StringComparison.Ordinal) == true);
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
    public async Task AdminSendMessage_PersistsToPlayerStory_WhenDiscordIsUnavailable()
    {
        var playerId = $"dm-message-{Guid.NewGuid():N}";
        var createResponse = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId,
            name = "Message Recipient",
            race = "Human",
            @class = "Warrior",
            statMethod = "StandardArray"
        });
        createResponse.EnsureSuccessStatusCode();

        const string message = "A private word from the Dungeon Master, delivered without a Discord raven.";
        var response = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/send-message", new
        {
            playerId,
            message
        });

        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, receipt.GetProperty("sent").GetInt32());
        Assert.False(receipt.GetProperty("discordMirrored").GetBoolean());

        var storyResponse = await _userClient.GetAsync($"/api/dashboard/story?playerId={playerId}&limit=5");
        storyResponse.EnsureSuccessStatusCode();
        var story = await storyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(story.EnumerateArray(), entry => entry.GetProperty("narration").GetString() == message);
    }

    [Fact]
    public async Task CoDmPlayerFlowMessage_RequiresRequestMarkerAndCollapsesRetries()
    {
        var playerId = $"codm-message-{Guid.NewGuid():N}";
        var createResponse = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId,
            name = "Co-DM Recipient",
            race = "Human",
            @class = "Ranger",
            statMethod = "StandardArray"
        });
        createResponse.EnsureSuccessStatusCode();

        var payload = new
        {
            requestId = $"request-{Guid.NewGuid():N}",
            playerId,
            message = "One message, even when the courier knocks twice.",
            delivery = "player_flow"
        };
        var missingMarker = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/co-dm/messages", payload);
        Assert.Equal(HttpStatusCode.BadRequest, missingMarker.StatusCode);
        using var unauthorizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/admin/co-dm/messages")
        {
            Content = JsonContent.Create(payload)
        };
        unauthorizedRequest.Headers.Add("X-GAE-Request", "co-dm");
        var unauthorized = await _userClient.SendAsync(unauthorizedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);

        var first = await PostCoDmAsync("/api/dashboard/admin/co-dm/messages", payload);
        var retry = await PostCoDmAsync("/api/dashboard/admin/co-dm/messages", payload);
        first.EnsureSuccessStatusCode();
        retry.EnsureSuccessStatusCode();
        var firstReceipt = await first.Content.ReadFromJsonAsync<JsonElement>();
        var retryReceipt = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstReceipt.GetProperty("id").GetString(), retryReceipt.GetProperty("id").GetString());

        var story = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/story?playerId={playerId}&limit=10");
        Assert.Single(story.EnumerateArray(), entry => entry.GetProperty("narration").GetString() == payload.message);
    }

    [Fact]
    public async Task CoDmProposal_RequiresOneTimeApprovalNonceAndAuditsDecision()
    {
        var playerId = $"codm-proposal-{Guid.NewGuid():N}";
        var createResponse = await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId,
            name = "Co-DM Proposal Target",
            race = "Human",
            @class = "Ranger",
            statMethod = "StandardArray"
        });
        createResponse.EnsureSuccessStatusCode();
        var before = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/players/{playerId}");
        var originalGold = before.GetProperty("gold").GetInt32();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var proposalResponse = await PostCoDmAsync("/api/dashboard/admin/co-dm/proposals", new
        {
            requestId = $"request-{Guid.NewGuid():N}",
            approvalToken = token,
            playerId,
            kind = "adjust_resources",
            title = "Award one audit coin",
            rationale = "Deterministic approval and replay test.",
            evidenceIds = new[] { playerId },
            goldDelta = 1
        });
        Assert.Equal(HttpStatusCode.Created, proposalResponse.StatusCode);
        var proposal = await proposalResponse.Content.ReadFromJsonAsync<JsonElement>();
        var actionId = proposal.GetProperty("id").GetString();
        Assert.Equal("pending", proposal.GetProperty("status").GetString());
        Assert.False(proposal.TryGetProperty("approvalToken", out _));

        var stillUnchanged = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/players/{playerId}");
        Assert.Equal(originalGold, stillUnchanged.GetProperty("gold").GetInt32());

        var wrongToken = await PostCoDmAsync($"/api/dashboard/admin/co-dm/actions/{actionId}/approve", new { approvalToken = new string('0', 64) });
        Assert.Equal(HttpStatusCode.Forbidden, wrongToken.StatusCode);

        var approved = await PostCoDmAsync($"/api/dashboard/admin/co-dm/actions/{actionId}/approve", new { approvalToken = token });
        approved.EnsureSuccessStatusCode();
        var replay = await PostCoDmAsync($"/api/dashboard/admin/co-dm/actions/{actionId}/approve", new { approvalToken = token });
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        var after = await _adminClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/players/{playerId}");
        Assert.Equal(originalGold + 1, after.GetProperty("gold").GetInt32());

        var audit = await _adminClient.GetFromJsonAsync<JsonElement>("/api/dashboard/admin/co-dm/actions");
        var action = audit.EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == actionId);
        Assert.Equal("approved", action.GetProperty("status").GetString());
        Assert.Equal(GaeWebApplicationFactory.DefaultAdminUsername, action.GetProperty("decidedBy").GetString());
        Assert.Equal(playerId, action.GetProperty("playerId").GetString());
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

        var equippedBladeId = player.GetProperty("equipment").GetProperty("mainHand").GetProperty("id").GetString();
        var unequipResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/item-action", new
        {
            playerId = "mutation-player-1",
            itemId = equippedBladeId,
            action = "unequip"
        });
        unequipResponse.EnsureSuccessStatusCode();

        playerResponse = await _userClient.GetAsync("/api/dashboard/players/mutation-player-1");
        playerResponse.EnsureSuccessStatusCode();
        player = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, player.GetProperty("equipment").GetProperty("mainHand").ValueKind);
        Assert.Contains(player.GetProperty("inventory").EnumerateArray(), item => item.GetProperty("name").GetString() == "Debug Blade");

        var removeResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/item-action", new
        {
            playerId = "mutation-player-1",
            itemId = equippedBladeId,
            action = "remove"
        });
        removeResponse.EnsureSuccessStatusCode();

        playerResponse = await _userClient.GetAsync("/api/dashboard/players/mutation-player-1");
        playerResponse.EnsureSuccessStatusCode();
        player = await playerResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(player.GetProperty("inventory").EnumerateArray(), item => item.GetProperty("name").GetString() == "Debug Blade");
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

    [Fact]
    public async Task AdminRegistryItemUpsert_AcceptsCamelCaseTwoHandedPayload()
    {
        var response = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/registry/items", new
        {
            id = "itest-two-handed-blade",
            name = "Integration Claymore",
            description = "Two-handed registry upsert coverage.",
            type = "Weapon",
            value = 99,
            isEquippable = true,
            isTwoHanded = true
        });
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(created.GetProperty("isTwoHanded").GetBoolean());

        var getResponse = await _adminClient.GetAsync("/api/dashboard/admin/registry/items/itest-two-handed-blade");
        getResponse.EnsureSuccessStatusCode();

        var item = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(item.GetProperty("isTwoHanded").GetBoolean());
    }

    [Fact]
    public async Task AdminTeleport_ClearsActiveInteractionState()
    {
        await _userClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId = "mutation-player-3",
            name = "Conversation Target",
            race = "Human",
            @class = "Warrior",
            statMethod = "StandardArray"
        });

        var talkResponse = await _userClient.PostAsJsonAsync("/api/dashboard/action", new
        {
            playerId = "mutation-player-3",
            command = "talk to mara"
        });
        talkResponse.EnsureSuccessStatusCode();

        var teleportResponse = await _adminClient.PostAsJsonAsync("/api/dashboard/admin/mutations/teleport", new
        {
            playerId = "mutation-player-3",
            roomId = "qa-reset-room",
            roomName = "QA Reset Room",
            roomDescription = "Admin-created reset room.",
            createRoomIfMissing = true,
            connectFromCurrentRoom = false
        });
        teleportResponse.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var stateManager = scope.ServiceProvider.GetRequiredService<IStateManager>();
        var player = await stateManager.GetPlayerAsync("mutation-player-3");

        Assert.NotNull(player);
        Assert.Equal(InteractionMode.Explore, player!.Interaction.Mode);
        Assert.Null(player.Interaction.Target);

        var lookResponse = await _userClient.PostAsJsonAsync("/api/dashboard/action", new
        {
            playerId = "mutation-player-3",
            command = "look"
        });
        lookResponse.EnsureSuccessStatusCode();

        var lookResult = await lookResponse.Content.ReadFromJsonAsync<JsonElement>();
        var summary = lookResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("QA Reset Room", summary);
        Assert.DoesNotContain("Conversation with", summary);
    }
}
