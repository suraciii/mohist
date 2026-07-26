using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public sealed class EventPushDispatcherSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DispatchAsync_OffersPersistedEventToPushQueueWithoutDurableHandler()
    {
        var events = new FakeEventStore();
        var queue = new RecordingPushQueue();
        var dispatcher = BuildDispatcher(events, new FakeDeadLetterStore(), [], queue);
        events.Enqueue(FakeEventStore.Build(
            EventCatalog.ReverseDns.IssueCompleted,
            "/mohist/issues/issue_push",
            eventId: "evt_push"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal("evt_push", Assert.Single(queue.Events).Id);
        Assert.Single(events.Marked);
    }

    [Fact]
    public async Task DispatchAsync_PushHandlerFailure_SettlesSourceAndDoesNotBlockLaterEvent()
    {
        var options = Options.Create(new EventDispatcherOptions
        {
            PushQueueCapacity = 2,
            PushDeliveryTimeout = TimeSpan.FromMinutes(1),
        });
        var events = new FakeEventStore();
        var deadLetters = new FakeDeadLetterStore();
        var pushFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new EventPushQueue(options, NullLogger<EventPushQueue>.Instance);
        var worker = new EventPushWorker(
            queue,
            [new EventPushSubscription(
                "com.mohist.*",
                new FailingPushHandler(pushFailed),
                static (handler, evt, ct) => ((FailingPushHandler)handler).HandleAsync(evt, ct),
                "failing-push")],
            new FakeTimeProvider(StartTime),
            options,
            NullLogger<EventPushWorker>.Instance);
        var dispatcher = BuildDispatcher(events, deadLetters, [], queue);
        const string source = "/mohist/issues/issue_push_failure";
        events.Enqueue(FakeEventStore.Build(EventCatalog.ReverseDns.IssueCompleted, source, id: 1, eventId: "evt-1"));
        events.Enqueue(FakeEventStore.Build(EventCatalog.ReverseDns.IssueCompleted, source, id: 2, eventId: "evt-2"));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await dispatcher.DispatchAsync(CancellationToken.None);
            await pushFailed.Task;

            Assert.Equal([1L, 2L], events.Marked.Select(item => item.Id));
            Assert.Empty(events.PendingUndelivered);
            Assert.Empty(deadLetters.Written);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static EventDispatcherService BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subscriptions,
        IEventPushQueue pushQueue)
    {
        deadLetters.EventStore = events;
        return new EventDispatcherService(
            events,
            subscriptions,
            deadLetters,
            new FakeTimeProvider(StartTime),
            Options.Create(new EventDispatcherOptions()),
            NullLogger<EventDispatcherService>.Instance,
            pushQueue);
    }

    private sealed class RecordingPushQueue : IEventPushQueue
    {
        public List<CloudEvent> Events { get; } = [];

        public bool TryEnqueue(CloudEvent evt)
        {
            Events.Add(evt);
            return true;
        }
    }

    private sealed class FailingPushHandler(TaskCompletionSource failed)
    {
        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            failed.TrySetResult();
            throw new InvalidOperationException("push failed");
        }
    }
}
