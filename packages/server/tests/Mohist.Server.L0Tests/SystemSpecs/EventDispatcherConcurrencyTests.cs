using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.L0Tests.Support;
using Xunit;

namespace Mohist.Server.L0Tests.SystemSpecs;

/// <summary>
/// Concurrency contract of the stream-lease engine: a stream lease is
/// exclusive per owner. While one owner holds a claim and is inside a
/// handler, a second owner's claim on the same stream is rejected and the
/// event is dispatched exactly once. Correctness comes from the lease
/// store's claim semantics, not from a single-threaded actor.
/// </summary>
[Trait("level", "L0")]
public class EventDispatcherConcurrencyTests
{
    [Fact]
    public async Task ClaimAndDrainOneAsync_SecondOwnerCannotClaimHeldStream_DispatchesOnce()
    {
        var events = new FakeEventStore();
        var deadLetters = new FakeDeadLetterStore { EventStore = events };
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var subscription = new Subscription(
            "test.event",
            new object(),
            async (_, _, ct) =>
            {
                calls++;
                handlerEntered.SetResult();
                await releaseHandler.Task.WaitAsync(ct);
            });
        var dispatcher = new EventDispatcherService(
            events,
            [subscription],
            deadLetters,
            new FakeDispatchStreamLeaseStore(),
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            Options.Create(new EventDispatcherOptions()),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
        events.Enqueue(FakeEventStore.Build("test.event", "/test/source"));

        var firstDrain = dispatcher.ClaimAndDrainOneAsync("owner-a", CancellationToken.None);
        await handlerEntered.Task;

        // The stream lease is held by owner-a; owner-b gets no claim and
        // must not wait on the in-flight handler — it returns immediately.
        var secondClaim = dispatcher.ClaimAndDrainOneAsync("owner-b", CancellationToken.None);
        Assert.True(secondClaim.IsCompleted);
        Assert.False(await secondClaim);

        releaseHandler.SetResult();
        await firstDrain;

        Assert.Equal(1, calls);
        Assert.Single(events.Marked);
    }
}
