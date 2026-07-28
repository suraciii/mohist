using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Focused unit coverage for the T-002 settlement-state retention
/// contract: <see cref="EventDispatcherService"/> keeps its in-process
/// per-handler state when <see cref="Microsoft.Extensions.DependencyInjection.Mohist.Server.Infrastructure.Data.Events.IEventStore.MarkDispatchedAsync"/>
/// or <see cref="IDeadLetterStore.SettleAsync"/> throws, so the next
/// cycle retries only the settlement write without re-invoking already
/// completed handlers or resetting a dead-lettered handler's attempt
/// count. Spec: <c>openspec/changes/issue-502/specs/event-dispatcher/spec.md
/// §Delivery-settlement-preserves-in-process-retry-progress-until-durable</c>.
/// </summary>
public class EventDispatcherSettlementRetentionSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string IssueCompleted = EventCatalog.ReverseDns.IssueCompleted;

    private static EventDispatcherService BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subs,
        FakeTimeProvider time,
        int handlerMaxAttempts = 3,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        deadLetters.EventStore = events;
        return new(
            events,
            subs,
            deadLetters,
            time,
            Options.Create(new EventDispatcherOptions
            {
                BatchSize = 100,
                MaxAttempts = handlerMaxAttempts,
                BaseBackoff = baseBackoff ?? TimeSpan.FromSeconds(1),
                MaxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30),
            }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
    }

    [Fact]
    public async Task DispatchAsync_DeadLetterSettlementFailure_RecoveryReusesHandlerState()
    {
        // One completed handler sits alongside a dead-lettered handler. The
        // settlement write throws; the dispatcher's in-process state must
        // survive so the next cycle settles the row without calling either
        // handler again and without resetting the dead-letter attempt count.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore { ThrowAfterSourceMark = true };
        var goodCalls = 0;
        var badCalls = 0;
        var good = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => goodCalls++),
            DispatchDynamic);
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badCalls++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 1);

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_recovery",
            id: 1,
            eventId: "evt_recovery"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CancellationToken.None));

        Assert.Equal(1, goodCalls);
        Assert.Equal(1, badCalls);
        Assert.Empty(events.Marked);
        Assert.Empty(dlq.Written);
        Assert.Single(events.PendingUndelivered);

        dlq.ThrowAfterSourceMark = false;
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(1, goodCalls);
        Assert.Equal(1, badCalls);
        Assert.Single(events.Marked);
        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal("evt_recovery", deadLetter.EventId);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Empty(events.PendingUndelivered);

        await dispatcher.DispatchAsync(CancellationToken.None);
        Assert.Equal(1, goodCalls);
        Assert.Equal(1, badCalls);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_MarkFailure_RecoveryReusesHandlerState()
    {
        // A single handler succeeds; the source mark then throws. The
        // next cycle must settle the row without reinvoking the already
        // completed handler.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore { ThrowOnMark = _ => true };
        var dlq = new FakeDeadLetterStore();
        var calls = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => calls++),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_mark_recovery",
            id: 1,
            eventId: "evt_mark_recovery"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);

        events.ThrowOnMark = null;
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Single(events.Marked);
        Assert.Empty(events.PendingUndelivered);
    }

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }
}
