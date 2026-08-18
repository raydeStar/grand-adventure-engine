using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Dashboard.Api.Services;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Integration.Tests;

/// <summary>
/// The deferral contract: a slow narrator must not stall the turn. The mechanical result goes out
/// immediately with a placeholder, the prose is recorded as owed, and it is delivered when it lands.
/// </summary>
public class DeferredNarrationTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task AFastNarratorIsNotDeferred()
    {
        var queue = new RecordingQueue();
        var delivery = new RecordingDelivery();
        var inner = BuildNarrator(delay: TimeSpan.Zero, "The door swings wide.");
        var deferring = new DeferringNarratorService(inner.Object, queue, delivery,
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        var narration = await deferring.NarrateActionAsync(BuildContext());

        Assert.Equal("The door swings wide.", narration);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task ASlowNarratorReturnsAPlaceholderImmediately()
    {
        var queue = new RecordingQueue();
        var delivery = new RecordingDelivery();
        var inner = BuildNarrator(TimeSpan.FromSeconds(3), "Eventually, prose.");
        var deferring = new DeferringNarratorService(inner.Object, queue, delivery,
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        var started = DateTimeOffset.UtcNow;
        var narration = await deferring.NarrateActionAsync(BuildContext());
        var elapsed = DateTimeOffset.UtcNow - started;

        // The whole point: the player is not made to wait for the slow model.
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Returning took {elapsed}; it should not wait for the narrator.");
        Assert.NotEqual("Eventually, prose.", narration);
        Assert.False(string.IsNullOrWhiteSpace(narration));
        Assert.Single(queue.Enqueued);
    }

    /// <summary>The debt is recorded before returning, so a crash cannot lose the paragraph.</summary>
    [Fact]
    public async Task TheOwedNarrationIsRecordedWithASnapshotOfItsContext()
    {
        var queue = new RecordingQueue();
        var inner = BuildNarrator(TimeSpan.FromSeconds(3), "Later.");
        var deferring = new DeferringNarratorService(inner.Object, queue, new RecordingDelivery(),
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        await deferring.NarrateActionAsync(BuildContext());

        var pending = Assert.Single(queue.Enqueued);
        Assert.Equal("act-1", pending.ActionId);
        Assert.Equal("player-1", pending.PlayerId);
        Assert.Equal("tavern", pending.RoomId);
        // The prompt inputs must travel with the item; narrating from live state later would
        // describe whatever room the player had wandered into by then.
        Assert.Contains("open the cellar door", pending.ContextJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>When the slow call finally lands, the prose is delivered without the worker involved.</summary>
    [Fact]
    public async Task WhenTheNarratorFinishes_TheProseIsDelivered()
    {
        var queue = new RecordingQueue();
        var delivery = new RecordingDelivery();
        var inner = BuildNarrator(TimeSpan.FromMilliseconds(400), "The cellar exhales cold air.");
        var deferring = new DeferringNarratorService(inner.Object, queue, delivery,
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        await deferring.NarrateActionAsync(BuildContext());

        var delivered = await delivery.WaitForFirstAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("The cellar exhales cold air.", delivered.Narration);
        Assert.Equal("act-1", delivered.ActionId);
        Assert.Single(queue.Completed);
    }

    /// <summary>A narrator that throws leaves the item for the worker to retry, not silently dropped.</summary>
    [Fact]
    public async Task WhenTheNarratorFails_TheItemIsLeftForRetry()
    {
        var queue = new RecordingQueue();
        var inner = new Mock<INarratorService>();
        inner.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(400);
                throw new InvalidOperationException("narrator exploded");
            });

        var deferring = new DeferringNarratorService(inner.Object, queue, new RecordingDelivery(),
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        await deferring.NarrateActionAsync(BuildContext());

        var failed = await queue.WaitForFailureAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("narrator exploded", failed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Structured calls must never be deferred: the engine acts on the disposition and interaction
    /// mode a conversation turn returns, so a placeholder would corrupt game state.
    /// </summary>
    [Fact]
    public async Task ConversationTurnsAreNeverDeferred()
    {
        var queue = new RecordingQueue();
        var inner = new Mock<INarratorService>();
        inner.Setup(n => n.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(600);
                return new FreeFormResponse { Success = true, Narration = "\"Aye?\"" };
            });

        var deferring = new DeferringNarratorService(inner.Object, queue, new RecordingDelivery(),
            NullLogger<DeferringNarratorService>.Instance, ShortBudget);

        var response = await deferring.ProcessConversationTurnAsync(
            new PlayerCharacter { Id = "player-1", Name = "Bonk" },
            new Room { Id = "tavern", Name = "Tavern" },
            new Npc { Id = "mara", Name = "Mara" },
            new InteractionState { Mode = InteractionMode.Conversation, Target = "Mara" },
            "hello");

        Assert.Equal("\"Aye?\"", response.Narration);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task AZeroBudgetDisablesDeferralEntirely()
    {
        var queue = new RecordingQueue();
        var inner = BuildNarrator(TimeSpan.FromMilliseconds(300), "Blocking prose.");
        var deferring = new DeferringNarratorService(inner.Object, queue, new RecordingDelivery(),
            NullLogger<DeferringNarratorService>.Instance, TimeSpan.Zero);

        var narration = await deferring.NarrateActionAsync(BuildContext());

        Assert.Equal("Blocking prose.", narration);
        Assert.Empty(queue.Enqueued);
    }

    // ── Harness ──

    private static Mock<INarratorService> BuildNarrator(TimeSpan delay, string narration)
    {
        var mock = new Mock<INarratorService>();
        mock.Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                return narration;
            });
        return mock;
    }

    private static NarratorContext BuildContext() => new()
    {
        Player = new PlayerCharacter { Id = "player-1", Name = "Bonk", Race = "Human", Class = "Warrior" },
        CurrentRoom = new Room { Id = "tavern", Name = "The Rusted Flagon", Description = "A creaky tavern." },
        Action = new GameAction { Id = "act-1", PlayerId = "player-1", RawInput = "open the cellar door", Type = ActionType.Unknown },
        MechanicalResult = new ActionResult { ActionId = "act-1", Success = true, MechanicalSummary = "The door opens." },
        RecentStory = []
    };

    private sealed class RecordingQueue : INarrationQueue
    {
        private readonly TaskCompletionSource<string> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<PendingNarration> Enqueued { get; } = [];
        public List<PendingNarration> Completed { get; } = [];

        public Task<PendingNarration> EnqueueAsync(PendingNarration pending, CancellationToken ct = default)
        {
            lock (Enqueued) Enqueued.Add(pending);
            return Task.FromResult(pending);
        }

        public Task CompleteAsync(PendingNarration pending, string narration, CancellationToken ct = default)
        {
            pending.Complete(narration);
            lock (Completed) Completed.Add(pending);
            return Task.CompletedTask;
        }

        public Task FailAsync(PendingNarration pending, string error, CancellationToken ct = default)
        {
            _failure.TrySetResult(error);
            return Task.CompletedTask;
        }

        public async Task<string> WaitForFailureAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_failure.Task, Task.Delay(timeout));
            Assert.True(completed == _failure.Task, "No failure was recorded before the timeout.");
            return await _failure.Task;
        }

        public Task<PendingNarration?> ClaimNextAsync(CancellationToken ct = default) => Task.FromResult<PendingNarration?>(null);
        public Task<IReadOnlyList<PendingNarration>> GetOutstandingForPlayerAsync(string playerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PendingNarration>>([]);
        public Task<long> NextSequenceAsync(string playerId, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<int> RecoverStaleInFlightAsync(TimeSpan olderThan, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed record DeliveredNarration(string ActionId, string PlayerId, string? RoomId, string Narration);

    private sealed class RecordingDelivery : INarrationDelivery
    {
        private readonly TaskCompletionSource<DeliveredNarration> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<DeliveredNarration> Delivered { get; } = [];

        public Task DeliverAsync(string actionId, string playerId, string? roomId, string narration, CancellationToken ct = default)
        {
            var record = new DeliveredNarration(actionId, playerId, roomId, narration);
            lock (Delivered) Delivered.Add(record);
            _first.TrySetResult(record);
            return Task.CompletedTask;
        }

        public async Task<DeliveredNarration> WaitForFirstAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_first.Task, Task.Delay(timeout));
            Assert.True(completed == _first.Task, "No narration was delivered before the timeout.");
            return await _first.Task;
        }
    }
}
