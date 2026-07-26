using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public sealed class EventPushSpecs
{
    [Fact]
    public void EventPushQueue_DropsWhenCapacityIsFull()
    {
        var queue = new EventPushQueue(
            Options.Create(new EventDispatcherOptions { PushQueueCapacity = 1 }),
            NullLogger<EventPushQueue>.Instance);

        Assert.True(queue.TryEnqueue(BuildEvent("evt-1")));
        Assert.False(queue.TryEnqueue(BuildEvent("evt-2")));
    }

    [Fact]
    public async Task EventPushWorker_ContainsHandlerFailureAndContinues()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new[]
        {
            new EventPushSubscription(
                "com.mohist.*",
                new DelegatePushHandler(_ => throw new InvalidOperationException("push failed")),
                static (handler, evt, ct) => ((DelegatePushHandler)handler).HandleAsync(evt, ct),
                "failing-push"),
            new EventPushSubscription(
                "com.mohist.*",
                new DelegatePushHandler(_ => observed.SetResult()),
                static (handler, evt, ct) => ((DelegatePushHandler)handler).HandleAsync(evt, ct),
                "following-push"),
        };
        var worker = BuildWorker(subscriptions, new FakeTimeProvider());

        await worker.DeliverAsync(BuildEvent("evt-failure"), CancellationToken.None);

        await observed.Task;
    }

    [Fact]
    public async Task EventPushWorker_UsesFakeTimeProviderForTimeoutCancellation()
    {
        var time = new FakeTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new[]
        {
            new EventPushSubscription(
                "com.mohist.*",
                new DelegatePushHandler(async ct =>
                {
                    started.SetResult();
                    try
                    {
                        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                            .Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled.SetResult();
                        throw;
                    }
                }),
                static (handler, evt, ct) => ((DelegatePushHandler)handler).HandleAsync(evt, ct),
                "timeout-push"),
        };
        var worker = BuildWorker(subscriptions, time, TimeSpan.FromMinutes(1));
        var delivery = worker.DeliverAsync(BuildEvent("evt-timeout"), CancellationToken.None);

        await started.Task;
        time.Advance(TimeSpan.FromMinutes(1));
        await cancelled.Task;
        await delivery;
    }

    private static EventPushWorker BuildWorker(
        IEnumerable<EventPushSubscription> subscriptions,
        TimeProvider time,
        TimeSpan? timeout = null) =>
        new(
            new EventPushQueue(
                Options.Create(new EventDispatcherOptions { PushQueueCapacity = 2 }),
                NullLogger<EventPushQueue>.Instance),
            subscriptions,
            time,
            Options.Create(new EventDispatcherOptions
            {
                PushQueueCapacity = 2,
                PushDeliveryTimeout = timeout ?? TimeSpan.FromMinutes(1),
            }),
            NullLogger<EventPushWorker>.Instance);

    private static CloudEvent BuildEvent(string id) =>
        new(id, new Uri("/mohist/test", UriKind.Relative), "com.mohist.test", TestTime.UtcNow, null);

    private sealed class DelegatePushHandler
    {
        private readonly Func<CancellationToken, Task> _handler;

        public DelegatePushHandler(Action<CancellationToken> handler)
        {
            _handler = ct =>
            {
                handler(ct);
                return Task.CompletedTask;
            };
        }

        public DelegatePushHandler(Func<CancellationToken, Task> handler) => _handler = handler;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct) => _handler(ct);
    }
}
