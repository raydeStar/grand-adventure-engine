using System.Net.Http.Json;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
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

    private async Task<QuestDefinition> GetSpawnQuestAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
        var registry = scope.ServiceProvider.GetRequiredService<IContentRegistryService>();

        var spawn = await state.GetRoomAsync("spawn");
        var questId = spawn!.Npcs.First(n => n.QuestsOffered.Count > 0).QuestsOffered[0];
        return registry.Quests.GetAll().First(q => q.Id.Equals(questId, StringComparison.OrdinalIgnoreCase));
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
        var quest = await GetSpawnQuestAsync();

        var acceptResult = await PostActionAsync(playerId, $"accept quest {quest.Name}");
        Assert.True(acceptResult.GetProperty("success").GetBoolean());

        var acceptSummary = acceptResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains(quest.Name, acceptSummary, StringComparison.OrdinalIgnoreCase);

        var journalResult = await PostActionAsync(playerId, "journal");
        var journalSummary = journalResult.GetProperty("mechanicalSummary").GetString()!;
        Assert.Contains(quest.Name, journalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuestInfo_ActiveQuest_ShowsDetails()
    {
        var playerId = "quest-info-flow";
        await CreateTestCharacterAsync(playerId);
        var quest = await GetSpawnQuestAsync();

        await PostActionAsync(playerId, $"accept quest {quest.Name}");

        var infoResult = await PostActionAsync(playerId, $"quest {quest.Name}");
        Assert.True(infoResult.GetProperty("success").GetBoolean());

        var summary = infoResult.GetProperty("mechanicalSummary").GetString()!;
        var firstObjectiveText = quest.Stages[0].Objectives[0].Description ?? quest.Stages[0].Objectives[0].Id;
        Assert.Contains(quest.Name, summary, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            summary.Contains(quest.Stages[0].Name, StringComparison.OrdinalIgnoreCase)
            || summary.Contains(firstObjectiveText, StringComparison.OrdinalIgnoreCase),
            $"Expected quest info to mention the active stage or objective. Got: {summary}");
    }

    [Fact]
    public async Task AbandonQuest_ActiveQuest_RemovesFromJournal()
    {
        var playerId = "quest-abandon-flow";
        await CreateTestCharacterAsync(playerId);
        var quest = await GetSpawnQuestAsync();

        await PostActionAsync(playerId, $"accept quest {quest.Name}");

        var abandonResult = await PostActionAsync(playerId, $"abandon quest {quest.Name}");
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
        var quest = await GetSpawnQuestAsync();

        await PostActionAsync(playerId, $"accept quest {quest.Name}");

        var dupeResult = await PostActionAsync(playerId, $"accept quest {quest.Name}");
        var summary = dupeResult.GetProperty("mechanicalSummary").GetString()!;

        Assert.True(
            summary.Contains("already", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("active", StringComparison.OrdinalIgnoreCase) ||
            !dupeResult.GetProperty("success").GetBoolean(),
            $"Expected duplicate accept to be handled gracefully. Got: {summary}");
    }
}
