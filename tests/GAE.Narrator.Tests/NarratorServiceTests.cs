using System.Net;
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
    public async Task ProcessFreeFormAsync_ForLowStakesAction_ResolvesLocally()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("HTTP should not be called for low-stakes local free-form resolution."));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A repeatable manual test fixture room.", Npcs = [new Npc { Id = "sentinel", Name = "Sentinel" }] },
            "pick nose",
            []);

        Assert.True(response.Success);
        Assert.Empty(response.StatChanges);
        Assert.Empty(response.InventoryChanges);
        Assert.Empty(response.EntityChanges);
        Assert.Contains("Sentinel", response.Narration);
        Assert.DoesNotContain("mug", response.Narration, StringComparison.OrdinalIgnoreCase);
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
