using System.Net.Http.Json;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

/// <summary>
/// Integration tests for the quest system: accept, journal, quest info,
/// abandon, and quest-related commands through the HTTP API.
/// </summary>
public class QuestFlowTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public QuestFlowTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateUserClient();
    }

    private async Task<PlayerCharacter> CreateTestCharacterAsync(string playerId)
    {
        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IGameEngine>();

        var concept = new CharacterConcept
        {
            PlayerDiscordId = playerId,
            Name = "Quester McQuestface",
            Race = "Human",
            Class = "Fighter",
            StatMethod = StatAllocationMethod.StandardArray,
            Backstory = "Born to quest."
        };

        return await engine.CreateCharacterFromConceptAsync(concept);
    }

    private async Task<JsonElement> PostActionAsync(string playerId, string command)
    {
        var response = await _client.PostAsJsonAsync("/api/dashboard/action", new
        {
            PlayerId = playerId,
            Command = command
        });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Journal_EmptyQuestLog_ReturnsNoActiveQuests()
    {
        await CreateTestCharacterAsync("quest-journal-empty");

        var result = await PostActionAsync("quest-journal-empty", "journal");
        Assert.True(result.GetProperty("success").GetBoolean());

        var summary = result.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("quest journal is empty", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptQuest_ValidQuest_AppearsInJournal()
    {
        var playerId = "quest-accept-flow";
        await CreateTestCharacterAsync(playerId);

        // Accept a quest available in the spawn room
        var acceptResult = await PostActionAsync(playerId, "accept rat problem");
        Assert.True(acceptResult.GetProperty("success").GetBoolean());

        var acceptSummary = acceptResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Rat Problem", acceptSummary, StringComparison.OrdinalIgnoreCase);

        // Verify it shows in journal
        var journalResult = await PostActionAsync(playerId, "journal");
        var journalSummary = journalResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Rat Problem", journalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuestInfo_ActiveQuest_ShowsDetails()
    {
        var playerId = "quest-info-flow";
        await CreateTestCharacterAsync(playerId);

        await PostActionAsync(playerId, "accept rat problem");

        var infoResult = await PostActionAsync(playerId, "quest rat problem");
        Assert.True(infoResult.GetProperty("success").GetBoolean());

        var summary = infoResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Rat Problem", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Clear the Cellar", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbandonQuest_ActiveQuest_RemovesFromJournal()
    {
        var playerId = "quest-abandon-flow";
        await CreateTestCharacterAsync(playerId);

        await PostActionAsync(playerId, "accept rat problem");

        var abandonResult = await PostActionAsync(playerId, "abandon rat problem");
        Assert.True(abandonResult.GetProperty("success").GetBoolean());

        var abandonSummary = abandonResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("Abandoned", abandonSummary, StringComparison.OrdinalIgnoreCase);

        // Verify it's gone from journal
        var journalResult = await PostActionAsync(playerId, "journal");
        var journalSummary = journalResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains("quest journal is empty", journalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptQuest_AlreadyAccepted_FailsGracefully()
    {
        var playerId = "quest-dup-accept";
        await CreateTestCharacterAsync(playerId);

        await PostActionAsync(playerId, "accept rat problem");

        // Try to accept again
        var dupeResult = await PostActionAsync(playerId, "accept rat problem");
        var summary = dupeResult.GetProperty("mechanicalSummary").GetString()!;

        // Should indicate the quest is already active or accepted
        Assert.True(
            summary.Contains("already", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("active", StringComparison.OrdinalIgnoreCase) ||
            !dupeResult.GetProperty("success").GetBoolean(),
            $"Expected duplicate accept to be handled gracefully. Got: {summary}");
    }
}
