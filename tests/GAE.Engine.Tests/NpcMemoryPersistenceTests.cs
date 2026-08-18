using GAE.Core.Models;

namespace GAE.Engine.Tests;

/// <summary>
/// NPCs must carry what happened between them and a player across conversations and sessions.
/// The reported failure: flirt with the barkeep, be rebuffed, walk out, walk back in, and be
/// greeted as a stranger.
/// </summary>
public class NpcMemoryPersistenceTests
{
    // ── The Mara scenario: a mood must outlive the conversation ──

    /// <summary>
    /// The narrator returns a plain disposition word. That word used to be written only to the
    /// in-memory flat field, never to the rich state that is persisted per player, so every
    /// reaction was forgotten on leaving.
    /// </summary>
    [Fact]
    public void FlatDispositionFromNarrator_MovesThePersistedRichState()
    {
        var state = new NpcDispositionState { Emotion = "neutral", Intensity = 55, Baseline = 55 };

        state.ApplyFlatDisposition("annoyed");

        Assert.Equal("annoyed", state.Emotion);
        Assert.True(state.Intensity < 55, $"Expected annoyance to lower intensity, got {state.Intensity}.");
    }

    [Theory]
    [InlineData("hostile", 55)]
    [InlineData("angry", 55)]
    [InlineData("annoyed", 55)]
    public void NegativeDispositions_LowerIntensity(string flat, int startingIntensity)
    {
        var state = new NpcDispositionState { Emotion = "neutral", Intensity = startingIntensity, Baseline = startingIntensity };

        state.ApplyFlatDisposition(flat);

        Assert.True(state.Intensity < startingIntensity, $"{flat} should lower intensity from {startingIntensity}, got {state.Intensity}.");
    }

    [Theory]
    [InlineData("friendly")]
    [InlineData("flirtatious")]
    [InlineData("grateful")]
    public void PositiveDispositions_RaiseIntensity(string flat)
    {
        var state = new NpcDispositionState { Emotion = "neutral", Intensity = 50, Baseline = 50 };

        state.ApplyFlatDisposition(flat);

        Assert.True(state.Intensity > 50, $"{flat} should raise intensity from 50, got {state.Intensity}.");
    }

    /// <summary>One word must not wipe out an established relationship in a single turn.</summary>
    [Fact]
    public void ASingleDispositionWord_DoesNotFullyOverwriteHistory()
    {
        var devoted = new NpcDispositionState { Emotion = "friendly", Intensity = 95, Baseline = 65 };

        devoted.ApplyFlatDisposition("annoyed");

        Assert.InRange(devoted.Intensity, 36, 80);
    }

    /// <summary>The adverbs ToFlatDisposition writes must round-trip back in.</summary>
    [Fact]
    public void IntensityAdverbs_AreUnderstoodOnTheWayBack()
    {
        var mild = new NpcDispositionState { Emotion = "neutral", Intensity = 55, Baseline = 55 };
        var severe = new NpcDispositionState { Emotion = "neutral", Intensity = 55, Baseline = 55 };

        mild.ApplyFlatDisposition("slightly annoyed");
        severe.ApplyFlatDisposition("overwhelmingly angry");

        Assert.True(severe.Intensity < mild.Intensity,
            $"Overwhelming anger ({severe.Intensity}) should sit below slight annoyance ({mild.Intensity}).");
    }

    [Fact]
    public void UnknownDispositionWord_LeavesIntensityAlone()
    {
        var state = new NpcDispositionState { Emotion = "neutral", Intensity = 60, Baseline = 60 };

        state.ApplyFlatDisposition("bewildered-by-tuesday");

        Assert.Equal(60, state.Intensity);
    }

    // ── The ledger ──

    [Fact]
    public void RepeatedIdenticalMemories_StrengthenRatherThanStack()
    {
        var ledger = new NpcMemoryLedger();
        var entry = () => new NpcMemoryEntry { Summary = "They insulted me.", Weight = 50 };

        ledger.Remember(entry());
        ledger.Remember(entry());

        Assert.Single(ledger.Entries);
        Assert.True(ledger.Entries[0].Weight > 50);
    }

    [Fact]
    public void OrdinaryMemoriesFadeWithTime_CoreOnesNever()
    {
        var ledger = new NpcMemoryLedger();
        ledger.Remember(new NpcMemoryEntry { Summary = "They asked about the weather.", Weight = 40, IsCore = false });
        ledger.Remember(new NpcMemoryEntry { Summary = "They betrayed me.", Weight = 95, IsCore = true });

        ledger.Forget(TimeSpan.FromDays(60));

        Assert.DoesNotContain(ledger.Entries, e => e.Summary.Contains("weather", StringComparison.Ordinal));
        Assert.Contains(ledger.Entries, e => e.Summary.Contains("betrayed", StringComparison.Ordinal));
    }

    [Fact]
    public void RecentOrdinaryMemories_SurviveShortAbsences()
    {
        var ledger = new NpcMemoryLedger();
        ledger.Remember(new NpcMemoryEntry { Summary = "They bought me a drink.", Weight = 60 });

        ledger.Forget(TimeSpan.FromHours(2));

        Assert.Single(ledger.Entries);
    }

    /// <summary>The ledger is prompt budget, so it must stay bounded however much happens.</summary>
    [Fact]
    public void LedgerStaysBounded_AndKeepsCoreMemoriesUnderPressure()
    {
        var ledger = new NpcMemoryLedger();
        ledger.Remember(new NpcMemoryEntry { Summary = "They saved my life.", Weight = 90, IsCore = true });

        for (var i = 0; i < 40; i++)
            ledger.Remember(new NpcMemoryEntry { Summary = $"Idle chatter number {i}.", Weight = 30 });

        var direct = ledger.Entries.Count(e => e.Source == NpcMemorySource.Direct);
        Assert.True(direct <= NpcMemoryLedger.MaxDirectEntries, $"Expected at most {NpcMemoryLedger.MaxDirectEntries} direct memories, got {direct}.");
        Assert.Contains(ledger.Entries, e => e.Summary.Contains("saved my life", StringComparison.Ordinal));
    }

    [Fact]
    public void GossipIsCappedSeparatelyFromFirsthandMemory()
    {
        var ledger = new NpcMemoryLedger();
        for (var i = 0; i < 20; i++)
            ledger.Remember(new NpcMemoryEntry { Summary = $"Word going round number {i}.", Weight = 25, Source = NpcMemorySource.Gossip });

        var gossip = ledger.Entries.Count(e => e.Source == NpcMemorySource.Gossip);
        Assert.True(gossip <= NpcMemoryLedger.MaxGossipEntries, $"Expected at most {NpcMemoryLedger.MaxGossipEntries} gossip memories, got {gossip}.");
    }

    // ── Promises: the Pete failure ──

    /// <summary>
    /// Pete offered a story in exchange for a drink, was paid, and never delivered. An unfulfilled
    /// offer has to survive as a debt the NPC knows they owe.
    /// </summary>
    [Fact]
    public void UnfulfilledOffers_PersistAsDebtsAndAppearInThePrompt()
    {
        var ledger = new NpcMemoryLedger();
        ledger.PromiseSomething("Something I offered Meow Meow and have not delivered: buy me a drink an' I'll tell you somethin'");

        var block = ledger.BuildPromptBlock("Meow Meow");

        Assert.NotNull(block);
        Assert.Contains("You still owe them", block, StringComparison.Ordinal);
        Assert.Contains("Do not restate the offer as if it were new", block, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePromises_AreNotRecordedTwice()
    {
        var ledger = new NpcMemoryLedger();
        ledger.PromiseSomething("a story about the boss");
        ledger.PromiseSomething("a story about the boss");

        Assert.Single(ledger.OpenPromises);
    }

    [Fact]
    public void SettledPromises_StopBeingBroughtUp()
    {
        var ledger = new NpcMemoryLedger();
        ledger.PromiseSomething("a story about the dungeon boss");

        ledger.SettlePromise("dungeon boss");

        Assert.Empty(ledger.OpenPromises);
    }

    // ── Prompt rendering ──

    [Fact]
    public void AStrangerProducesNoMemoryBlock()
    {
        Assert.Null(new NpcMemoryLedger().BuildPromptBlock("Meow Meow"));
    }

    [Fact]
    public void AReturningPlayerIsDescribedAsKnown()
    {
        var ledger = new NpcMemoryLedger { EncounterCount = 3, LastSpokeAt = DateTimeOffset.UtcNow.AddHours(-2) };
        ledger.Remember(new NpcMemoryEntry { Summary = "They flirted with me and I did not care for it.", Weight = 60 });

        var block = ledger.BuildPromptBlock("Meow Meow");

        Assert.NotNull(block);
        Assert.Contains("3 times before", block, StringComparison.Ordinal);
        Assert.Contains("flirted", block, StringComparison.Ordinal);
    }

    /// <summary>Hearsay must be marked as such so an NPC does not claim to have witnessed it.</summary>
    [Fact]
    public void GossipIsLabelledAsSecondHandInThePrompt()
    {
        var ledger = new NpcMemoryLedger { EncounterCount = 1 };
        ledger.Remember(new NpcMemoryEntry
        {
            Summary = "Word from Mara: they were rude to her.",
            Weight = 30,
            Source = NpcMemorySource.Gossip
        });

        var block = ledger.BuildPromptBlock("Meow Meow");

        Assert.Contains("second-hand", block!, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscussedTopicsAreListedSoNewsIsNotRepeated()
    {
        var ledger = new NpcMemoryLedger { EncounterCount = 1 };
        ledger.NoteTopic("the collapsed mine");

        var block = ledger.BuildPromptBlock("Meow Meow");

        Assert.Contains("Already discussed", block!, StringComparison.Ordinal);
        Assert.Contains("collapsed mine", block!, StringComparison.Ordinal);
    }

    [Fact]
    public void TopicsAreDeduplicatedAndBounded()
    {
        var ledger = new NpcMemoryLedger();
        ledger.NoteTopic("the mine");
        ledger.NoteTopic("The Mine");

        Assert.Single(ledger.TopicsDiscussed);

        for (var i = 0; i < 40; i++)
            ledger.NoteTopic($"topic {i}");

        Assert.True(ledger.TopicsDiscussed.Count <= NpcMemoryLedger.MaxTopics);
    }

    /// <summary>
    /// The block is injected into every conversation prompt on a local model, so it has to stay
    /// small even for a player with a long history.
    /// </summary>
    [Fact]
    public void PromptBlockStaysCompactEvenWithHeavyHistory()
    {
        var ledger = new NpcMemoryLedger { EncounterCount = 25, LastSpokeAt = DateTimeOffset.UtcNow };
        for (var i = 0; i < 40; i++)
            ledger.Remember(new NpcMemoryEntry { Summary = $"A moderately long recollection of event number {i} that happened.", Weight = 50 });
        for (var i = 0; i < 10; i++)
            ledger.NoteTopic($"subject {i}");
        ledger.PromiseSomething("a favour still outstanding");

        var block = ledger.BuildPromptBlock("Meow Meow")!;

        // Roughly 250 tokens of headroom; the point is that it cannot grow without bound.
        Assert.True(block.Length < 1000, $"Memory block grew to {block.Length} characters.");
    }
}
