using System.Net;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Narrator.Tests;

public class NarratorServiceTests
{
    [Fact]
    public async Task NarrateActionAsync_WhenHttpFailsOnNonLookAction_ReturnsFallback()
    {
        // Arrange: create a handler that always throws
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var context = new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room { Id = "test", Name = "Test Room", Description = "A test room." },
            Action = new GameAction { PlayerId = "p1", RawInput = "attack goblin", Type = ActionType.Attack },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You strike the goblin." }
        };

        // Act
        var result = await narrator.NarrateActionAsync(context);

        // Assert: should get contextual fallback narration, not throw
        Assert.NotNull(result);
        Assert.Contains("Test", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clears his throat", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateRoomAsync_WhenHttpFails_ReturnsFallbackRoom()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var sourceRoom = new Room { Id = "start", Name = "Start", EnvironmentTags = ["forest"] };

        var result = await narrator.GenerateRoomAsync("new_room", "north", sourceRoom);

        Assert.Equal("new_room", result.Id);
        Assert.NotNull(result.Name);
        Assert.Contains("south", result.Exits.Keys); // should have exit back
    }

    [Fact]
    public async Task GenerateBackstoryAsync_WhenHttpFails_ReturnsFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var concept = new CharacterConcept { Name = "Test", Race = "Elf", Class = "Mage" };

        var result = await narrator.GenerateBackstoryAsync(concept);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenHttpFailsOnConsequentialAction_ReturnsInWorldFailureFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior", Level = 1, Hp = 10, MaxHp = 10, Mp = 5, MaxMp = 5 },
            new Room { Id = "lab", Name = "QA Lab", Description = "A repeatable manual test fixture room." },
            "pick up the sword and stab the guard",
            []);

        Assert.False(response.Success);
        Assert.DoesNotContain("nothing dramatic happens", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrator", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hard logic", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenHttpFailsOnHarmlessAction_ReturnsPlayableLocalFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            new Room { Id = "gate", Name = "Ironhold Gate", Description = "A rusted war gate with iron plates.", Npcs = [new Npc { Id = "guard", Name = "Gate Warden" }] },
            "I want to shine the rusted gate",
            []);

        Assert.True(response.Success);
        Assert.Empty(response.StatChanges);
        Assert.Empty(response.InventoryChanges);
        Assert.Empty(response.EntityChanges);
        Assert.Contains("rusted gate", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrator", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenModelWrapsJsonInPreamble_StillParsesResponse()
    {
        var handler = new ResponseHttpMessageHandler("""
            {
              "choices": [
                {
                  "message": {
                    "content": "Here is your result:\n{\"narration\":\"Thorin rubs at the gate until a muted shine peers through the rust.\",\"success\":true,\"statChanges\":{},\"inventoryChanges\":[],\"entityChanges\":[],\"combatInitiated\":false,\"roomChanges\":null}"
                  }
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            new Room { Id = "gate", Name = "Ironhold Gate", Description = "A rusted war gate with iron plates." },
            "shine the rusted gate",
            []);

        Assert.True(response.Success);
        Assert.Contains("muted shine", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateActionAsync_ForLook_UsesGroundedRoomNarrationWithoutCallingHttp()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("HTTP should not be called for deterministic look narration."));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room
            {
                Id = "lab",
                Name = "QA Lab",
                Description = "A repeatable manual test fixture room. The walls hum with patient machinery.",
                Exits = new Dictionary<string, string> { ["south"] = "spawn" },
                Npcs = [new Npc { Id = "sentinel", Name = "Sentinel" }],
                Items = [new InventoryItem { Id = "token", Name = "Inspection Token", Quantity = 2 }]
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "look", Type = ActionType.Look },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = string.Empty }
        });

        Assert.Contains("QA Lab", narration);
        Assert.Contains("Sentinel", narration);
        Assert.Contains("Inspection Token", narration);
        Assert.Contains("south", narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mechanical", narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateActionAsync_ForMissingLookTarget_AvoidsTemplateFailureText()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("HTTP should not be called for deterministic look narration."));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room
            {
                Id = "lab",
                Name = "QA Lab",
                Description = "A repeatable manual test fixture room.",
                Items = [new InventoryItem { Id = "token", Name = "Inspection Token", Quantity = 2 }]
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "look at sentinel", Type = ActionType.Look, Target = "sentinel" },
            MechanicalResult = new ActionResult { Success = false, MechanicalSummary = "Look target 'sentinel' was not found in the current room." }
        });

        Assert.Contains("sentinel", narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing here answers that description", narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithBrokenDialogueQuotes_RepairsAndParses()
    {
        // Simulates the exact broken JSON that arrives after CompletionOrThrowAsync
        // deserializes the outer LM Studio response — dialogue quotes are bare/unescaped,
        // and the LLM returns "narrative" instead of "narration".
        var brokenJson = """{"narrative": ""Rumors?" Mara says, leaning closer. "Don't you start with that nonsense, kid," she mutters.", "success": true, "statChanges": {}, "inventoryChanges": [], "entityChanges": [], "combatInitiated": false, "interactionUpdate": {"mode": "conversation", "npcDisposition": "annoyed"}}""";

        var result = NarratorService.TryParseFreeFormResponse(brokenJson, out var response);

        Assert.True(result);
        Assert.Contains("Rumors", response.Narration);
        Assert.Contains("Mara", response.Narration);
        Assert.True(response.Success);
        Assert.NotNull(response.InteractionUpdate);
    }

    [Fact]
    public async Task ProcessConversationTurnAsync_WhenLlmReturnsDialogueQuotes_ParsesSuccessfully()
    {
        // Simulate the exact broken pattern: after outer JSON deserialization by
        // CompletionOrThrowAsync, the inner JSON has bare unescaped dialogue quotes like:
        //   {"narrative": ""Rumors?" Mara says.", "success": true}
        // We build the outer response so that the content field, when deserialized,
        // produces this broken inner JSON.
        //
        // The broken inner JSON (what TryParseFreeFormResponse receives):
        var brokenInner = """{"narrative": ""Rumors?" Mara says, leaning closer. "Don't you start with that nonsense, kid," she mutters.", "success": true, "statChanges": {}, "inventoryChanges": [], "entityChanges": [], "combatInitiated": false, "interactionUpdate": {"mode": "conversation", "npcDisposition": "annoyed"}}""";
        // Wrap it in a proper LM Studio response — JsonSerializer.Serialize escapes quotes
        // so that after outer deserialization we get the broken inner JSON back.
        var outerJson = "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(brokenInner) + "}}]}";
        var handler = new ResponseHttpMessageHandler(outerJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        // Pass a specific model name to skip model resolution (avoids extra HTTP call)
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, model: "test-model");

        var response = await narrator.ProcessConversationTurnAsync(
            new PlayerCharacter { Name = "Zephyr", Race = "Human", Class = "Rogue", Level = 1 },
            new Room { Id = "tavern", Name = "Tavern", Description = "A smoky tavern." },
            new Npc { Id = "mara", Name = "Mara", Personality = "Gruff barmaid" },
            new InteractionState { Mode = InteractionMode.Conversation, Target = "mara" },
            "flirt with Mara");

        Assert.NotNull(response);
        Assert.Contains("Rumors", response.Narration);
        Assert.DoesNotContain("noncommittal", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessConversationTurnAsync_WhenLlmReturnsNarrativeKey_NormalizesToNarration()
    {
        // LLM returns "narrative" instead of "narration" — should still parse
        var handler = new ResponseHttpMessageHandler("""
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"narrative\": \"The barkeep nods slowly.\", \"success\": true, \"statChanges\": {}, \"inventoryChanges\": [], \"entityChanges\": []}"
                  }
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessConversationTurnAsync(
            new PlayerCharacter { Name = "Zephyr", Race = "Human", Class = "Rogue", Level = 1 },
            new Room { Id = "tavern", Name = "Tavern", Description = "A smoky tavern." },
            new Npc { Id = "barkeep", Name = "Barkeep", Personality = "Friendly" },
            new InteractionState { Mode = InteractionMode.Conversation, Target = "barkeep" },
            "ask about rumors");

        Assert.NotNull(response);
        Assert.Contains("barkeep", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("noncommittal", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_ForLowStakesAction_WhenHttpFails_ReturnsFallback()
    {
        // Low-stakes actions now go through the LLM for humorous narration.
        // When LLM is offline, the fallback still handles them gracefully.
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A repeatable manual test fixture room.", Npcs = [new Npc { Id = "sentinel", Name = "Sentinel" }] },
            "pick nose",
            []);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Narration);
    }

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public FakeHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private class ResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public ResponseHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }
}
