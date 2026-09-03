using System.Text.Json;
using Mohist.Server.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Agent.Subscriptions;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Platform;

/// <summary>
/// Unit specs for <see cref="EventDispatcherService"/>: the stream-lease
/// drain engine that dispatch workers drive. All paths run against fakes
/// so the spec surface stays under the unit ceiling (&lt; 50ms / test, no
/// real silo, no real time, no real database — lease semantics use the
/// in-memory fake; the SQL lease store is covered by application-host tests).
/// </summary>
[Trait("level", "L0")] public class EventDispatcherSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string IssueCompleted = EventCatalog.ReverseDns.IssueCompleted;
    private const string IssueCancelled = EventCatalog.ReverseDns.IssueCancelled;
    private const string AnyEvent = "test.anything";

    private static EventDispatcherService BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subs,
        FakeTimeProvider time,
        int handlerMaxAttempts = 3,
        int streamPassLimit = 200,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        deadLetters.EventStore = events;
        return new(
            events,
            subs,
            deadLetters,
            new FakeDispatchStreamLeaseStore(),
            time,
            Options.Create(new EventDispatcherOptions
            {
                MaxEventsPerStreamPass = streamPassLimit,
                MaxAttempts = handlerMaxAttempts,
                BaseBackoff = baseBackoff ?? TimeSpan.FromSeconds(1),
                MaxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30),
            }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
    }

    [Fact]
    public async Task DrainAsync_PullsUndeliveredRow_MatchesHandler_InvokesAndMarks()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var calls = new List<string>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(evt => calls.Add(evt.Id)),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(
            type: IssueCompleted,
            source: "/mohist/issues/issue_1",
            id: 1,
            eventId: "evt_1"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(new[] { "evt_1" }, calls);
        var marked = Assert.Single(events.Marked);
        Assert.Equal(EventOrigin.Issue, marked.Origin);
        Assert.Equal("/mohist/issues/issue_1", marked.Source);
        Assert.Equal(1, marked.Id);
        Assert.Equal(StartTime, marked.DispatchedAt);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_NoUndeliveredRows_CompletesWithoutInvokingHandler()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var calls = new List<string>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(evt => calls.Add(evt.Id)),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Empty(calls);
        Assert.Empty(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_DispatchesPerStreamFifo_NoSkipNoReorder()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var seen = new List<string>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(evt => seen.Add(evt.Id)),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        // Two streams, several ids each. Stage out of order so the
        // per-stream Id sort is the only thing keeping FIFO correct.
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 3, eventId: "A_3"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_B", id: 2, eventId: "B_2"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 1, eventId: "A_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_B", id: 1, eventId: "B_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 2, eventId: "A_2"));

        await dispatcher.DrainAsync(CancellationToken.None);

        // Cross-stream order is not a contract; per-stream FIFO is.
        Assert.Equal(new[] { "A_1", "A_2", "A_3" }, seen.Where(id => id.StartsWith("A_", StringComparison.Ordinal)));
        Assert.Equal(new[] { "B_1", "B_2" }, seen.Where(id => id.StartsWith("B_", StringComparison.Ordinal)));
        Assert.Equal(5, events.Marked.Count);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_ParkedStream_DoesNotBlockOtherStreams()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var firstAttempt = true;
        var seen = new List<string>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(evt =>
            {
                if (evt.Id == "A_1" && firstAttempt)
                {
                    firstAttempt = false;
                    throw new InvalidOperationException("transient");
                }
                seen.Add(evt.Id);
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 1, eventId: "A_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 2, eventId: "A_2"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_B", id: 1, eventId: "B_1"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(["B_1"], seen);
        Assert.Equal(["B_1"], events.Marked.Select(ToStreamEventId));

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(["B_1", "A_1", "A_2"], seen);
        Assert.Equal(["B_1", "A_1", "A_2"], events.Marked.Select(ToStreamEventId));
    }

    private static string ToStreamEventId(FakeEventStore.RecordedDispatch dispatch) =>
        dispatch.Source.EndsWith("issue_A", StringComparison.Ordinal) ? $"A_{dispatch.Id}" : $"B_{dispatch.Id}";

    [Fact]
    public async Task DrainAsync_TransientFailure_RecoversOnSecondAttempt_StillMarksDelivered()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var attempts = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempts++;
                if (attempts < 2)
                    throw new InvalidOperationException("transient");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 3);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_1"));

        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(1, attempts);
        Assert.Empty(events.Marked);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_EventRetry_InvokesAllHandlersEachAttempt_AndDeadLettersFirstFailure()
    {
        // The retry unit is the event: every matching handler rides along
        // on each attempt (idempotency by EventId is the handler contract).
        // Exhaustion dead-letters the event once, recording the first
        // failing handler.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var goodAttempts = 0;
        var badAttempts = 0;
        var good = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => goodAttempts++),
            DispatchDynamic,
            "good-handler");
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badAttempts++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic,
            "bad-handler");
        var dispatcher = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_iso"));

        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(1, goodAttempts);
        Assert.Equal(1, badAttempts);
        Assert.Empty(events.Marked);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(2, goodAttempts);
        Assert.Equal(2, badAttempts);
        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal("bad-handler", deadLetter.FailingHandler);
        Assert.Single(events.Marked);
    }

    [Fact]
    public async Task DrainAsync_RoutingFailure_PropagatesToDispatcher_RetriesUntilDeadLetter()
    {
        // issue-363 T-002: subscription dispatch no longer swallows exceptions
        // locally. A launch failure must reach the dispatcher's retry/DLQ
        // path so the durable delivery semantics recover from transient
        // failures without losing events.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var handler = new RoutingDispatchHandler(
            new ThrowingScopeFactory(),
            NullLogger<RoutingDispatchHandler>.Instance);
        var dispatcher = BuildDispatcher(
            events,
            dlq,
            [new Subscription("*", handler, DispatchDynamic)],
            time,
            handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(
            type: IssueCompleted,
            source: "/mohist/issues/issue_agent_failure",
            eventId: "evt_agent_failure",
            extensions: new Dictionary<string, string> { ["projectid"] = "project_agent" }));

        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);
        Assert.Empty(dlq.Written);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal("evt_agent_failure", deadLetter.EventId);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Contains("launch unavailable", deadLetter.ErrorMessage);
        var marked = Assert.Single(events.Marked);
        Assert.Equal("/mohist/issues/issue_agent_failure", marked.Source);
        Assert.Empty(events.PendingUndelivered);

        // The row is gone from the queue so a subsequent drain must not
        // re-attempt the dispatch.
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_RoutingFailure_RangeEnvelopeIsStillNoOp()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var handler = new RoutingDispatchHandler(
            new ThrowingScopeFactory(),
            NullLogger<RoutingDispatchHandler>.Instance);
        var dispatcher = BuildDispatcher(
            events,
            dlq,
            [new Subscription("*", handler, DispatchDynamic)],
            time);

        events.Enqueue(FakeEventStore.Build(
            type: IssueCompleted,
            source: "/mohist/issues/issue_agent_no_projectid",
            eventId: "evt_agent_no_projectid"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Single(events.Marked);
        Assert.Empty(events.PendingUndelivered);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_ExhaustionWritesDeadLetter_MarksDispatched_AndStopsRetrying()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var attempts = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempts++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 3);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_poison"));

        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(1, attempts);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
        time.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(3, attempts);

        var dl = Assert.Single(dlq.Written);
        Assert.Equal("evt_poison", dl.EventId);
        Assert.Equal(IssueCompleted, dl.Type);
        Assert.Equal(3, dl.AttemptCount);
        Assert.Contains("permanent", dl.ErrorMessage);
        Assert.NotNull(dl.ErrorStack);
        Assert.Contains("InvalidOperationException", dl.ErrorStack);
        Assert.Contains("permanent", dl.ErrorStack);

        var marked = Assert.Single(events.Marked);
        Assert.Equal("/mohist/issues/issue_1", marked.Source);
        Assert.Equal(1, marked.Id);

        // Second drain must NOT re-deliver — the DLQ settlement dropped the
        // row from the undelivered queue.
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_DeadLetterSettlementFailure_ParksStream_RetriesSettlementNextPass()
    {
        // Settlement failure must not lose the event or the attempt budget:
        // the stream parks and the next pass retries only settlement.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore { ThrowAfterSourceMark = true };
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => throw new InvalidOperationException("permanent")),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_settlement_rollback",
            id: 1,
            eventId: "evt_settlement_rollback"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);
        Assert.Empty(dlq.Written);

        dlq.ThrowAfterSourceMark = false;
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
        Assert.Empty(events.PendingUndelivered);
    }

    [Fact]
    public async Task DrainAsync_DeliverBeforeSettleCrash_RowStaysUndelivered_AndIsRedelivered()
    {
        // At-least-once: a settle failure parks the stream; the next pass
        // re-invokes the handler (idempotent by EventId) and settles.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var deliveries = 0;
        var rec = new IdempotentRecorder(_ => deliveries++);
        var sub = new Subscription(IssueCompleted, rec, DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_crash"));
        events.ThrowOnMark = _ => true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DrainAsync(CancellationToken.None));

        Assert.Equal(1, deliveries);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);

        events.ThrowOnMark = null;
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        // Redelivered at least once more, then settled.
        Assert.True(deliveries >= 2);
        Assert.Single(events.Marked);
        Assert.Empty(events.PendingUndelivered);
    }

    [Fact]
    public async Task DrainAsync_SettleFailure_KeepsWholePassPending_AndRedrives()
    {
        // Settle is chunked and deferred: a settle failure leaves every
        // delivered-but-unsettled row pending; the next pass redelivers and
        // settles them (at-least-once amplification, bounded by the chunk).
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var seen = new List<string>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(evt => seen.Add(evt.Id)),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 2, eventId: "evt_2"));
        events.ThrowOnMark = evt => evt.Id == 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DrainAsync(CancellationToken.None));

        Assert.Equal(["evt_1", "evt_2"], seen);
        Assert.Equal([1L, 2L], events.PendingUndelivered.Select(evt => evt.Id));

        events.ThrowOnMark = null;
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        // Per-stream FIFO holds across the redrive.
        Assert.Equal(["evt_1", "evt_2", "evt_1", "evt_2"], seen);
        Assert.Equal([1L, 2L], events.Marked.Select(mark => mark.Id));
    }

    [Fact]
    public async Task DrainAsync_FanOutByType_MatchesClosedGenericHandler()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();

        var received = new List<CloudEvent<IssueCompleted>>();
        var handler = new CapturingTypedHandler(received);
        var sub = new Subscription(
            IssueCompleted,
            handler,
            MakeClosedGenericDelegate<IssueCompleted>(handler));

        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        var data = JsonSerializer.SerializeToElement(
            new IssueCompleted(WorkflowRunId: "wr_1"),
            CloudEvent.JsonOptions);
        events.Enqueue(FakeEventStore.Build(
            type: IssueCompleted,
            source: "/mohist/issues/issue_1",
            id: 1,
            eventId: "evt_typed",
            data: data));

        await dispatcher.DrainAsync(CancellationToken.None);

        var evt = Assert.Single(received);
        Assert.Equal(IssueCompleted, evt.Type);
        Assert.Equal("evt_typed", evt.Id);
        Assert.Equal("wr_1", evt.Data.WorkflowRunId);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_TypeMatcher_DoesNotInvokeNonMatchingSubscriptions()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var matchedCalls = 0;
        var otherCalls = 0;
        var matched = new Subscription(IssueCompleted, new Recorder(_ => matchedCalls++), DispatchDynamic);
        var other = new Subscription(IssueCancelled, new Recorder(_ => otherCalls++), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [matched, other], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_match"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(1, matchedCalls);
        Assert.Equal(0, otherCalls);
    }

    [Fact]
    public async Task DrainAsync_DeadLetterWriteFailure_KeepsRowUndelivered()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        dlq.ThrowOnWrite = _ => true;
        var attempts = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempts++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_dl_crash"));

        await dispatcher.DrainAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Empty(dlq.Written);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);
    }

    [Fact]
    public async Task DrainAsync_NoMatchingSubscription_MarksRowDeliveredWithoutInvoking()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription("test.never-matches", new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(AnyEvent, "/mohist/issues/issue_1", id: 1, eventId: "evt_orphan"));

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_DeadLettersOnlyFirstFailingHandler_AtExhaustion()
    {
        // One dead-letter row per exhausted event, recording the first
        // failing handler in subscription order.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var first = new FlakyRecorder(() => throw new InvalidOperationException("first poison"));
        var second = new Recorder(_ => throw new InvalidOperationException("second poison"));
        var dispatcher = BuildDispatcher(
            events,
            dlq,
            [
                new Subscription(IssueCompleted, first, DispatchDynamic, "first-handler"),
                new Subscription(IssueCompleted, second, DispatchDynamic, "second-handler"),
            ],
            time,
            handlerMaxAttempts: 2);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_two_poison",
            eventId: "evt_two_poison"));

        await dispatcher.DrainAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);

        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal("first-handler", deadLetter.FailingHandler);
        Assert.Equal(2, deadLetter.AttemptCount);
        Assert.Single(events.Marked);
    }

    [Fact]
    public async Task DrainAsync_DispatchedAtUsesInjectedTimeProvider()
    {
        var initial = StartTime;
        var later = initial.AddSeconds(42);
        var time = new FakeTimeProvider(initial);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_t1"));

        await dispatcher.DrainAsync(CancellationToken.None);
        time.SetUtcNow(later);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_2", id: 2, eventId: "evt_t2"));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Collection(
            events.Marked.OrderBy(m => m.Id),
            m => Assert.Equal(initial, m.DispatchedAt),
            m => Assert.Equal(later, m.DispatchedAt));
    }

    [Fact]
    public async Task RedeliverAsync_LoadsDeadLetterAndReDispatchesToMatchingHandlers()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var attempt = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempt++;
                if (attempt <= 3) throw new InvalidOperationException("poison");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 3);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_redeliver"));

        await dispatcher.DrainAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.DrainAsync(CancellationToken.None);
        var dl = Assert.Single(dlq.Written);
        Assert.Equal(3, attempt);

        var result = await dispatcher.RedeliverAsync(dl.DeadLetterId, CancellationToken.None);

        Assert.Equal(4, attempt);
        Assert.True(result.Found);
        Assert.True(result.Delivered);
        Assert.Equal(1, result.Attempts);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task RedeliverAsync_MissingDeadLetter_NoOps()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        var result = await dispatcher.RedeliverAsync(deadLetterId: 999, CancellationToken.None);

        Assert.False(result.Found);
        Assert.False(result.Delivered);
        Assert.Empty(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task RedeliverAsync_RetriesOnlyRecordedFailingHandler()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var goodCalls = 0;
        var badCalls = 0;
        var badStillFails = true;
        var good = new Subscription(IssueCompleted, new Recorder(_ => goodCalls++), DispatchDynamic, "good-handler");
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badCalls++;
                if (badStillFails)
                    throw new InvalidOperationException("poison");
            }),
            DispatchDynamic,
            "bad-handler");
        var dispatcher = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_redeliver_one"));
        await dispatcher.DrainAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);
        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal(2, goodCalls);
        Assert.Equal(2, badCalls);

        badStillFails = false;
        var result = await dispatcher.RedeliverAsync(deadLetter.DeadLetterId, CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Equal(2, goodCalls);
        Assert.Equal(3, badCalls);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task RedeliverAsync_ResolveFailureLeavesAmbiguousState_AndReplayIsIdempotent()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var poison = true;
        var sideEffects = new HashSet<string>(StringComparer.Ordinal);
        var handler = new Recorder(evt =>
        {
            if (poison)
                throw new InvalidOperationException("poison");
            sideEffects.Add(evt.Id);
        });
        var sub = new Subscription(IssueCompleted, handler, DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_redelivery_state",
            id: 1,
            eventId: "evt_redelivery_state"));
        await dispatcher.DrainAsync(CancellationToken.None);
        var deadLetter = Assert.Single(dlq.Written);

        poison = false;
        dlq.ThrowOnResolve = true;
        var ambiguous = await dispatcher.RedeliverAsync(deadLetter.DeadLetterId, CancellationToken.None);

        Assert.True(ambiguous.Found);
        Assert.False(ambiguous.Delivered);
        Assert.Single(sideEffects);
        Assert.Equal(
            DeadLetterStatus.Redelivering,
            (await dlq.GetAsync(deadLetter.DeadLetterId))!.Status);

        dlq.ThrowOnResolve = false;
        var result = await dispatcher.RedeliverAsync(deadLetter.DeadLetterId, CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Single(sideEffects);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task RedeliverAsync_AlreadyResolved_ReturnsFoundWithConflict()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);

        var row = new DeadLetterRow
        {
            DeadLetterId = 42,
            Origin = nameof(EventOrigin.Issue),
            Id = 1,
            Source = "/mohist/issues/issue_already_resolved",
            EventId = "evt_already_resolved",
            Type = IssueCompleted,
            Time = time.GetUtcNow(),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
            ExtensionsJson = "{}",
            FailingHandler = typeof(Recorder).FullName!,
            ErrorMessage = "poison",
            ErrorStack = "stack",
            AttemptCount = 1,
            DeadLetteredAt = time.GetUtcNow(),
        };
        await dlq.WriteAsync(row);
        var stored = await dlq.GetAsync(row.DeadLetterId);
        Assert.NotNull(stored);
        await dlq.ResolveAsync(stored!.DeadLetterId, time.GetUtcNow());

        var result = await dispatcher.RedeliverAsync(stored.DeadLetterId, CancellationToken.None);

        Assert.True(result.Found);
        Assert.False(result.Delivered);
        Assert.Contains("already resolved", result.Error, StringComparison.Ordinal);
        Assert.Empty(events.Marked);
    }

    [Fact]
    public async Task DrainAsync_BackoffSchedule_DoublesAndCapsAtMaxViaFakeTimeProvider()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var attemptTimes = new List<DateTimeOffset>();
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attemptTimes.Add(time.GetUtcNow());
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(
            events,
            dlq,
            [sub],
            time,
            handlerMaxAttempts: 5,
            baseBackoff: TimeSpan.FromSeconds(2),
            maxBackoff: TimeSpan.FromSeconds(5));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_backoff"));

        await dispatcher.DrainAsync(CancellationToken.None);
        var expectedDeltas = new[]
        {
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
        };
        foreach (var delta in expectedDeltas)
        {
            time.Advance(delta);
            await dispatcher.DrainAsync(CancellationToken.None);
        }

        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(expectedDeltas, attemptTimes.Zip(attemptTimes.Skip(1), (a, b) => b - a).ToArray());
        Assert.Equal(5, attemptTimes.Count);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DrainAsync_BackoffIsEventLevel_AllMatchingHandlersRideAlong()
    {
        // Backoff gates the event, not individual handlers: each attempt
        // re-invokes every matching handler from the top (idempotency by
        // EventId), and one attempt stops at the first failing handler —
        // the recorded head. A sibling that recovered early keeps riding
        // along until the slowest handler settles.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var firstAttempts = 0;
        var secondAttempts = 0;
        var first = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                firstAttempts++;
                if (firstAttempts < 3)
                    throw new InvalidOperationException("first transient");
            }),
            DispatchDynamic);
        var second = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                secondAttempts++;
                if (secondAttempts < 2)
                    throw new InvalidOperationException("second transient");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(
            events,
            dlq,
            [first, second],
            time,
            handlerMaxAttempts: 4,
            baseBackoff: TimeSpan.FromSeconds(2),
            maxBackoff: TimeSpan.FromSeconds(3));
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_cross_tick",
            eventId: "evt_cross_tick"));

        // Attempt 1: first fails, so second is not reached this attempt.
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(1, firstAttempts);
        Assert.Equal(0, secondAttempts);
        Assert.Empty(events.Marked);

        // Attempt 2 (after Backoff(1) = 2s): first still fails; second
        // still not reached.
        time.Advance(TimeSpan.FromSeconds(2));
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(2, firstAttempts);
        Assert.Equal(0, secondAttempts);
        Assert.Empty(events.Marked);

        // Attempt 3 (after Backoff(2) = 4s capped to 3s): first now
        // succeeds and is re-invoked even though it already ran; second
        // fails as the new head.
        time.Advance(TimeSpan.FromSeconds(3));
        await dispatcher.DrainAsync(CancellationToken.None);
        Assert.Equal(3, firstAttempts);
        Assert.Equal(1, secondAttempts);
        Assert.Empty(events.Marked);

        // Attempt 4 (after Backoff(3) = 8s capped to 3s): both succeed and
        // the event settles without dead-lettering.
        time.Advance(TimeSpan.FromSeconds(3));
        await dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(4, firstAttempts);
        Assert.Equal(2, secondAttempts);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public void EventDispatcherOptions_HaveRequiredDefaults()
    {
        var options = new EventDispatcherOptions();

        Assert.Equal(2, options.WorkerCount);
        Assert.Equal(TimeSpan.FromSeconds(30), options.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), options.SlowPollInterval);
        Assert.Equal(100, options.MaxStreamsPerPass);
        Assert.Equal(200, options.MaxEventsPerStreamPass);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(1), options.BaseBackoff);
        Assert.Equal(TimeSpan.FromSeconds(30), options.MaxBackoff);
        Assert.Equal(256, options.PushQueueCapacity);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PushDeliveryTimeout);
    }

    [Fact]
    public void Ctor_RejectsInvalidOptions()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);

        Assert.Throws<ArgumentOutOfRangeException>(() => new EventDispatcherService(
            events,
            [sub],
            dlq,
            new FakeDispatchStreamLeaseStore(),
            time,
            Options.Create(new EventDispatcherOptions { MaxStreamsPerPass = 0, MaxAttempts = 1 }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance));

        Assert.Throws<ArgumentOutOfRangeException>(() => new EventDispatcherService(
            events,
            [sub],
            dlq,
            new FakeDispatchStreamLeaseStore(),
            time,
            Options.Create(new EventDispatcherOptions { MaxAttempts = 0 }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance));
    }

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    private static DispatchDelegate MakeClosedGenericDelegate<TData>(ICloudEventHandler<TData> handler)
        where TData : class =>
        (rawHandler, evt, ct) =>
        {
            var typed = new CloudEvent<TData>(
                evt.Id, evt.Source, evt.Type, evt.Time,
                evt.Data!.Value.Deserialize<TData>(CloudEvent.JsonOptions)!,
                evt.DataContentType, evt.Subject, evt.SpecVersion, evt.Extensions);
            if (!handler.Filter(typed)) return Task.CompletedTask;
            return handler.HandleAsync(typed, ct);
        };
}
