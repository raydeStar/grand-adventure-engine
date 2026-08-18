using GAE.Core.Models;

namespace GAE.Engine.Tests;

/// <summary>
/// Rules for narration that is owed to a player. Mechanics have already been delivered, so this
/// queue must never lose prose silently, never deliver it out of order, and never retry a broken
/// narrator forever.
/// </summary>
public class PendingNarrationTests
{
    [Fact]
    public void AFailureSchedulesARetryRatherThanGivingUp()
    {
        var pending = new PendingNarration { ActionId = "a1", PlayerId = "p1" };

        pending.RecordFailure("connection refused");

        Assert.Equal(PendingNarrationStatus.Pending, pending.Status);
        Assert.Equal(1, pending.AttemptCount);
        Assert.True(pending.NextAttemptAt > DateTimeOffset.UtcNow, "A retry should be scheduled in the future.");
        Assert.Equal("connection refused", pending.LastError);
    }

    /// <summary>Backoff must grow, so a model that is down is not hammered every few seconds.</summary>
    [Fact]
    public void RetryDelayGrowsWithEachFailure()
    {
        var pending = new PendingNarration();

        pending.RecordFailure("first");
        var firstDelay = pending.NextAttemptAt - DateTimeOffset.UtcNow;

        pending.RecordFailure("second");
        var secondDelay = pending.NextAttemptAt - DateTimeOffset.UtcNow;

        Assert.True(secondDelay > firstDelay, $"Expected growing backoff, got {firstDelay} then {secondDelay}.");
    }

    /// <summary>
    /// A permanently broken narrator must not accumulate work forever. Once the budget is spent the
    /// placeholder the player already saw becomes the final text.
    /// </summary>
    [Fact]
    public void TheAttemptBudgetIsFinite()
    {
        var pending = new PendingNarration();

        for (var attempt = 0; attempt < PendingNarration.MaxAttempts; attempt++)
            pending.RecordFailure("still down");

        Assert.Equal(PendingNarrationStatus.Abandoned, pending.Status);
        Assert.NotNull(pending.CompletedAt);
    }

    [Fact]
    public void BackoffIsCappedSoRetriesNeverStopEntirely()
    {
        var pending = new PendingNarration();

        // Push attempts high enough that an uncapped exponential would run to hours.
        for (var attempt = 0; attempt < PendingNarration.MaxAttempts - 1; attempt++)
            pending.RecordFailure("down");

        Assert.True(pending.NextAttemptAt - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void CompletingStoresTheProseAndClearsTheError()
    {
        var pending = new PendingNarration();
        pending.RecordFailure("a transient blip");

        pending.Complete("The lantern gutters as you step inside.");

        Assert.Equal(PendingNarrationStatus.Completed, pending.Status);
        Assert.Equal("The lantern gutters as you step inside.", pending.Narration);
        Assert.Null(pending.LastError);
        Assert.NotNull(pending.CompletedAt);
    }

    /// <summary>
    /// The context is snapshotted at enqueue time. By the time turn two is narrated the player may be
    /// on turn five, so narrating from live state would describe the wrong scene.
    /// </summary>
    [Fact]
    public void TheContextSnapshotIsCarriedWithTheItem()
    {
        var pending = new PendingNarration
        {
            ActionId = "a1",
            PlayerId = "p1",
            ContextJson = """{"action":{"rawInput":"open the cellar door"}}"""
        };

        Assert.Contains("open the cellar door", pending.ContextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewItemIsImmediatelyDue()
    {
        var pending = new PendingNarration();

        Assert.Equal(PendingNarrationStatus.Pending, pending.Status);
        Assert.True(pending.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
