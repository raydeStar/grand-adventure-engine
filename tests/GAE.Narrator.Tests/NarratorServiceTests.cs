using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Narrator.Tests;

public class NarratorServiceTests
{
    [Fact]
    public async Task NarrateActionAsync_WhenHttpFails_ReturnsFallback()
    {
        // Arrange: create a handler that always throws
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var context = new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room { Id = "test", Name = "Test Room", Description = "A test room." },
            Action = new GameAction { PlayerId = "p1", RawInput = "look", Type = ActionType.Look },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You look around." }
        };

        // Act
        var result = await narrator.NarrateActionAsync(context);

        // Assert: should get fallback narration, not throw
        Assert.NotNull(result);
        Assert.Contains("narrator", result, StringComparison.OrdinalIgnoreCase);
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
}
