using System.Net.Http.Json;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

/// <summary>
/// Verifies that player game actions broadcast both ActionResult and GameEvent
/// via IGameEventBroadcaster (X04: SignalR live dashboard).
/// </summary>
public class SignalRBroadcastTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SignalRBroadcastTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    private async Task<PlayerCharacter> CreateTestCharacterAsync(string playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();

        return await engine.CreateCharacterFromConceptAsync(new CharacterConcept
        {
            PlayerDiscordId = playerId,
            Name = "SignalR Tester",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray,
            Backstory = "Testing broadcasts."
        });
    }

    private StubGameEventBroadcaster GetBroadcaster()
    {
        using var scope = _factory.Services.CreateScope();
        return (StubGameEventBroadcaster)scope.ServiceProvider.GetRequiredService<IGameEventBroadcaster>();
    }

    [Fact]
    public async Task PostAction_BroadcastsActionResult()
    {
        var player = await CreateTestCharacterAsync("signalr-action-1");
        var broadcaster = GetBroadcaster();
        var before = broadcaster.BroadcastedActions.Count;

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = player.Id,
            Command = "look"
        });
        response.EnsureSuccessStatusCode();

        Assert.True(broadcaster.BroadcastedActions.Count > before,
            "Expected ActionResult to be broadcast via SignalR");
        var last = broadcaster.BroadcastedActions[^1];
        Assert.Equal(player.Id, last.PlayerId);
        Assert.True(last.Result.Success);
    }

    [Fact]
    public async Task PostAction_BroadcastsGameEvent()
    {
        var player = await CreateTestCharacterAsync("signalr-event-1");
        var broadcaster = GetBroadcaster();
        var before = broadcaster.BroadcastedEvents.Count;

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = player.Id,
            Command = "look"
        });
        response.EnsureSuccessStatusCode();

        Assert.True(broadcaster.BroadcastedEvents.Count > before,
            "Expected GameEvent to be broadcast via SignalR");
        var last = broadcaster.BroadcastedEvents[^1];
        Assert.Equal(player.Id, last.PlayerId);
        Assert.Contains("look", last.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostAction_GameEvent_HasCorrectPlayerIdAndType()
    {
        var player = await CreateTestCharacterAsync("signalr-type-1");
        var broadcaster = GetBroadcaster();
        var before = broadcaster.BroadcastedEvents.Count;

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = player.Id,
            Command = "look"
        });
        response.EnsureSuccessStatusCode();

        var events = broadcaster.BroadcastedEvents.Skip(before).ToList();
        Assert.True(events.Count > 0);

        var evt = events[^1];
        Assert.Equal(player.Id, evt.PlayerId);
        // "look" doesn't move or start combat — should be StoryAdvanced
        Assert.Equal(GameEventType.StoryAdvanced, evt.Type);
        Assert.NotNull(evt.Data);
        Assert.Equal("look", evt.Data["command"]?.ToString());
    }

    [Fact]
    public async Task PostAction_GameEvent_ContainsCommandInSummary()
    {
        var player = await CreateTestCharacterAsync("signalr-cmd-1");
        var broadcaster = GetBroadcaster();
        var before = broadcaster.BroadcastedEvents.Count;

        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = player.Id,
            Command = "stats"
        });
        response.EnsureSuccessStatusCode();

        var events = broadcaster.BroadcastedEvents.Skip(before).ToList();
        Assert.True(events.Count > 0);
        Assert.Contains(events, e => e.Summary.Contains("stats", StringComparison.OrdinalIgnoreCase));
    }
}
