using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class EventDispatcherConcurrencyTests
{
    [Fact]
    public async Task DispatchAsync_OverlappingCalls_DispatchEventOnce()
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
            new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            Options.Create(new EventDispatcherOptions()),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
        events.Enqueue(FakeEventStore.Build("test.event", "/test/source"));

        var firstDispatch = dispatcher.DispatchAsync(CancellationToken.None);
        await handlerEntered.Task;

        var secondDispatch = dispatcher.DispatchAsync(CancellationToken.None);
        Assert.False(secondDispatch.IsCompleted);

        releaseHandler.SetResult();
        await Task.WhenAll(firstDispatch, secondDispatch);

        Assert.Equal(1, calls);
        Assert.Single(events.Marked);
    }
}
