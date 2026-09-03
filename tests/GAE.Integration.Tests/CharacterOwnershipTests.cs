using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GAE.Integration.Tests;

/// <summary>
/// A signed-in account may only see and drive the characters it created. Admins see everything.
/// </summary>
public class CharacterOwnershipTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;

    public CharacterOwnershipTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Register_SignsInAndRejectsDuplicatesAndReservedNames()
    {
        var name = $"reg-{Guid.NewGuid():N}"[..20];
        using var client = await _factory.CreateRegisteredClientAsync(name, "a-decent-password");

        var session = await client.GetFromJsonAsync<JsonElement>("/api/dashboard/auth/session");
        Assert.Equal(name, session.GetProperty("username").GetString());
        Assert.False(session.GetProperty("isAdmin").GetBoolean());

        using var anonymous = _factory.CreateClient();
        var duplicate = await anonymous.PostAsJsonAsync("/api/dashboard/auth/register", new { username = name.ToUpperInvariant(), password = "another-password" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var reserved = await anonymous.PostAsJsonAsync("/api/dashboard/auth/register", new { username = GaeWebApplicationFactory.DefaultAdminUsername, password = "another-password" });
        Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);

        var weak = await anonymous.PostAsJsonAsync("/api/dashboard/auth/register", new { username = $"w-{Guid.NewGuid():N}"[..12], password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var wrongPassword = await anonymous.PostAsJsonAsync("/api/dashboard/auth/login", new { username = name, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        var login = await anonymous.PostAsJsonAsync("/api/dashboard/auth/login", new { username = name, password = "a-decent-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Characters_AreInvisibleAndInertToOtherAccounts()
    {
        var owner = $"own-{Guid.NewGuid():N}"[..20];
        var stranger = $"str-{Guid.NewGuid():N}"[..20];
        using var ownerClient = await _factory.CreateRegisteredClientAsync(owner, "owner-password-1");
        using var strangerClient = await _factory.CreateRegisteredClientAsync(stranger, "stranger-password-1");
        using var adminClient = _factory.CreateAdminClient();

        var playerId = $"owned-{Guid.NewGuid():N}"[..24];
        var create = await ownerClient.PostAsJsonAsync("/api/dashboard/characters", new
        {
            playerId,
            name = "Owned Hero",
            race = "Human",
            @class = "Warrior",
            statMethod = "StandardArray",
            skipHeroIntro = true
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(owner, created.GetProperty("player").GetProperty("ownerId").GetString());

        // Owner sees and drives it.
        var ownerRead = await ownerClient.GetAsync($"/api/dashboard/players/{playerId}");
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        var ownerList = await ownerClient.GetFromJsonAsync<JsonElement>("/api/dashboard/players");
        Assert.Contains(ownerList.EnumerateArray(), p => p.GetProperty("id").GetString() == playerId);
        var ownerAction = await ownerClient.PostAsJsonAsync("/api/dashboard/action", new { playerId, command = "look" });
        Assert.Equal(HttpStatusCode.OK, ownerAction.StatusCode);
        var ownerStory = await ownerClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/story?playerId={playerId}");
        Assert.True(ownerStory.GetArrayLength() > 0);

        // Stranger cannot see, read, act, or view the story.
        var strangerList = await strangerClient.GetFromJsonAsync<JsonElement>("/api/dashboard/players");
        Assert.DoesNotContain(strangerList.EnumerateArray(), p => p.GetProperty("id").GetString() == playerId);
        var strangerRead = await strangerClient.GetAsync($"/api/dashboard/players/{playerId}");
        Assert.Equal(HttpStatusCode.NotFound, strangerRead.StatusCode);
        var strangerAction = await strangerClient.PostAsJsonAsync("/api/dashboard/action", new { playerId, command = "look" });
        Assert.Equal(HttpStatusCode.Forbidden, strangerAction.StatusCode);
        var strangerStory = await strangerClient.GetFromJsonAsync<JsonElement>($"/api/dashboard/story?playerId={playerId}");
        Assert.Equal(0, strangerStory.GetArrayLength());
        var strangerAllStory = await strangerClient.GetFromJsonAsync<JsonElement>("/api/dashboard/story?limit=500");
        Assert.DoesNotContain(strangerAllStory.EnumerateArray(), e => e.GetProperty("playerId").GetString() == playerId);
        var strangerRoom = await strangerClient.GetAsync($"/api/dashboard/rooms/spawn?playerId={playerId}");
        Assert.Equal(HttpStatusCode.NotFound, strangerRoom.StatusCode);

        // Admin still sees everything.
        var adminRead = await adminClient.GetAsync($"/api/dashboard/players/{playerId}");
        Assert.Equal(HttpStatusCode.OK, adminRead.StatusCode);
        var adminAction = await adminClient.PostAsJsonAsync("/api/dashboard/action", new { playerId, command = "look" });
        Assert.Equal(HttpStatusCode.OK, adminAction.StatusCode);
    }
}
