using System.Net;
using GAE.Core.Models;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Narrator.Tests;

/// <summary>
/// The narrator tries providers in order, so a cheap preferred backend can lead while a locally
/// hosted model stands behind it. A failing provider must be skipped for a cooldown rather than
/// retried every turn: the Codex adapter can burn a multi-minute timeout before failing, and paying
/// that on every action would make the game unplayable while it is down.
/// </summary>
public class ProviderChainTests
{
    [Fact]
    public void ChainIsPrimaryFirstThenFallbacks()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };

        var narrator = new NarratorService(
            http, NullLogger<NarratorService>.Instance,
            provider: "CodexCli",
            fallbackProviders: ["OpenAICompatible", "Ollama"]);

        Assert.Equal(["CodexCli", "OpenAICompatible", "Ollama"], narrator.ProviderChain);
    }

    [Fact]
    public void DuplicateAndBlankFallbacksAreIgnored()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };

        var narrator = new NarratorService(
            http, NullLogger<NarratorService>.Instance,
            provider: "OpenAICompatible",
            fallbackProviders: ["OpenAICompatible", "  ", "Ollama", "ollama"]);

        Assert.Equal(["OpenAICompatible", "Ollama"], narrator.ProviderChain);
    }

    [Fact]
    public void WithNoFallbacksConfigured_TheChainIsJustThePrimary()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };

        var narrator = new NarratorService(http, NullLogger<NarratorService>.Instance, provider: "OpenAICompatible");

        Assert.Equal(["OpenAICompatible"], narrator.ProviderChain);
    }

    /// <summary>
    /// With an unreachable Codex primary, narration must still be produced by the HTTP fallback rather
    /// than falling through to offline text.
    /// </summary>
    [Fact]
    public async Task WhenThePrimaryIsUnavailable_TheFallbackProducesTheNarration()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"The lantern gutters as you enter."}}]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };

        var narrator = new NarratorService(
            http, NullLogger<NarratorService>.Instance,
            // A path that cannot exist, so the Codex transport fails fast instead of waiting.
            provider: "CodexCli",
            codexExecutable: "gae-nonexistent-codex-binary",
            codexTimeoutSeconds: 30,
            fallbackProviders: ["OpenAICompatible"]);

        var narration = await narrator.NarrateActionAsync(BuildContext());

        Assert.Contains("lantern gutters", narration, StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.Requests > 0, "The HTTP fallback should have been called.");
    }

    /// <summary>
    /// After the primary fails it should be skipped for its cooldown, so a second turn goes straight
    /// to the working fallback instead of paying the broken provider's cost again.
    /// </summary>
    [Fact]
    public async Task AFailedPrimaryIsNotRetriedOnTheNextTurn()
    {
        using var handler = new CountingHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"Dust settles in the doorway."}}]}
            """);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };

        var narrator = new NarratorService(
            http, NullLogger<NarratorService>.Instance,
            provider: "CodexCli",
            codexExecutable: "gae-nonexistent-codex-binary",
            codexTimeoutSeconds: 30,
            fallbackProviders: ["OpenAICompatible"]);

        var firstStarted = DateTimeOffset.UtcNow;
        await narrator.NarrateActionAsync(BuildContext());
        var firstElapsed = DateTimeOffset.UtcNow - firstStarted;

        var secondStarted = DateTimeOffset.UtcNow;
        await narrator.NarrateActionAsync(BuildContext());
        var secondElapsed = DateTimeOffset.UtcNow - secondStarted;

        // The second turn must not be materially slower — it should skip the dead primary entirely.
        Assert.True(secondElapsed <= firstElapsed + TimeSpan.FromSeconds(2),
            $"Second turn took {secondElapsed} versus {firstElapsed}; the failed provider looks like it was retried.");
    }

    private static NarratorContext BuildContext() => new()
    {
        Player = new PlayerCharacter { Name = "Bonk", Race = "Human", Class = "Warrior" },
        CurrentRoom = new Room { Id = "inn", Name = "Lantern's Rest", Description = "A crowded inn." },
        Action = new GameAction { Id = "a1", PlayerId = "p1", RawInput = "push open the door", Type = ActionType.Unknown },
        MechanicalResult = new ActionResult { ActionId = "a1", Success = true, MechanicalSummary = "You push the door open." },
        RecentStory = []
    };

    /// <summary>Counts HTTP calls and returns a canned chat-completion body.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public int Requests { get; private set; }

        public CountingHandler(HttpStatusCode status, string body = """{"choices":[{"message":{"content":"Something happens."}}]}""")
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
