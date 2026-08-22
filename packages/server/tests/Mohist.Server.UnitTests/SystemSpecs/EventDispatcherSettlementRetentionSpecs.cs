using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.TestSupport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Focused unit coverage for settlement-failure retention in the stream-lease
/// engine: <see cref="EventDispatcherService"/> must never lose an event or
/// its durable attempt budget when a settlement write (source mark or
/// dead-letter settle) fails. The stream parks on its lease holding the
/// budget; the next pass retries delivery and settlement (handlers are
/// idempotent by EventId — re-invocation is accepted).
/// </summary>
public class EventDispatcherSettlementRetentionSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string IssueCompleted = EventCatalog.ReverseDns.IssueCompleted;

    private static (EventDispatcherService Dispatcher, FakeDispatchStreamLeaseStore Leases) BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subs,
        FakeTimeProvider time,
        int handlerMaxAttempts = 3)
    {
        deadLetters.EventStore = events;
        var leases = new FakeDispatchStreamLeaseStore();
        return (
            new EventDispatcherService(
                events,
                subs,
                deadLetters,
                leases,
                time,
                Options.Create(new EventDispatcherOptions
                {
                    MaxAttempts = handlerMaxAttempts,
                    BaseBackoff = TimeSpan.FromSeconds(1),
                    MaxBackoff = TimeSpan.FromSeconds(30),
                }),
                NullLogger<EventDispatcherService>.Instance,
                NullEventPushQueue.Instance),
            leases);
    }

    [Fact]
    public async Task DeadLetterSettleFailure_ParksWithBudget_NextPassRetriesSettlement()
    {
        // The head exhausts its budget and the dead-letter settlement write
        // throws. The stream parks holding the full budget (nothing marked,
        // nothing dead-lettered — the real settle is transactional), and the
        // next pass re-drives: handlers re-run (idempotent contract), the
        // settle succeeds, and the budget resets for the next head.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore { ThrowAfterSourceMark = true };
        var goodCalls = 0;
        var badCalls = 0;
        var good = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => goodCalls++),
            DispatchDynamic,
            "good-handler");
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badCalls++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic,
            "bad-handler");
        var (dispatcher, leases) = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 1);

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_recovery",
            id: 1,
            eventId: "evt_recovery"));

        await dispatcher.DrainAsync(CancellationToken.None);

        // Settle failed: nothing marked, nothing written, event retained,
        // and the parked lease keeps the exhausted budget.
        Assert.Equal(1, goodCalls);
        Assert.Equal(1, badCalls);
        Assert.Empty(events.Marked);
        Assert.Empty(dlq.Written);
        Assert.Single(events.PendingUndelivered);
        var parked = leases.Snapshot(nameof(EventOrigin.Issue), "/mohist/issues/issue_recovery");
        Assert.NotNull(parked);
        Assert.Equal(1, parked.Value.Attempts);

        // Backoff elapses, settle now succeeds. Handlers re-run (at-least-once),
        // the row is marked, one dead letter records the first failing handler,
        // and the budget resets — a later clean pass reuses the lease row.
        dlq.ThrowAfterSourceMark = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(2, goodCalls);
        Assert.Equal(2, badCalls);
        Assert.Single(events.Marked);
        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal("evt_recovery", deadLetter.EventId);
        Assert.Equal("bad-handler", deadLetter.FailingHandler);
        Assert.Equal(1, deadLetter.AttemptCount);
        Assert.Empty(events.PendingUndelivered);

        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(2, goodCalls);
        Assert.Equal(2, badCalls);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task MarkFailure_ParksWithBudget_NextPassRedeliversAndSettles()
    {
        // The handler succeeds but the source mark throws. The drain parks
        // (budget kept, event retained) and rethrows; the next pass
        // re-delivers and settles — the handler contract is idempotency by
        // EventId, so re-invocation is expected.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore { ThrowOnMark = _ => true };
        var dlq = new FakeDeadLetterStore();
        var calls = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => calls++),
            DispatchDynamic);
        var (dispatcher, leases) = BuildDispatcher(events, dlq, [sub], time);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_mark_recovery",
            id: 1,
            eventId: "evt_mark_recovery"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DrainAsync(CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);
        var parked = leases.Snapshot(nameof(EventOrigin.Issue), "/mohist/issues/issue_mark_recovery");
        Assert.NotNull(parked);
        Assert.True(parked.Value.Attempts >= 1);

        events.ThrowOnMark = null;
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(2, calls);
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
