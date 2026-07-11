using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Unit specs for <see cref="EventDispatcherService"/>: the pure-DI fan-out
/// core that the cluster-singleton <see cref="DispatcherGrain"/> delegates
/// to. All paths are driven against fakes so the spec surface stays under
/// the unit ceiling (&lt; 50ms / test, no real silo, no real time). Spec:
/// <c>openspec/changes/issue-362/specs/event-dispatch/spec.md</c>.
/// </summary>
public class EventDispatcherSpecs
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
        int batchLimit = 100)
    {
        deadLetters.EventStore = events;
        return new(
            events,
            subs,
            deadLetters,
            time,
            Options.Create(new DispatcherOptions
            {
                BatchLimit = batchLimit,
                HandlerMaxAttempts = handlerMaxAttempts,
            }),
            NullLogger<EventDispatcherService>.Instance);
    }

    [Fact]
    public async Task DispatchAsync_PullsUndeliveredRow_MatchesHandler_InvokesAndMarks()
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

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(new[] { "evt_1" }, calls);
        var marked = Assert.Single(events.Marked);
        Assert.Equal("/mohist/issues/issue_1", marked.Source);
        Assert.Equal(1, marked.Id);
        Assert.Equal(StartTime, marked.DispatchedAt);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_DispatchesPerStreamFifo_NoSkipNoReorder()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var seen = new List<long>();
        var sub = new Subscription(
            IssueCompleted,
            new Recorder(_ => seen.Add(0)),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        // Two streams, several ids each. Stage out of order so the
        // (Source, Id) sort is the only thing keeping FIFO correct.
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 3, eventId: "A_3"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_B", id: 2, eventId: "B_2"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 1, eventId: "A_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_B", id: 1, eventId: "B_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_A", id: 2, eventId: "A_2"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        // Source ordering first (issue_A < issue_B), then per-source id.
        Assert.Equal(
            new[] { "A_1", "A_2", "A_3", "B_1", "B_2" },
            events.Marked.Select(m => m.Source switch
            {
                "/mohist/issues/issue_A" => $"A_{m.Id}",
                "/mohist/issues/issue_B" => $"B_{m.Id}",
                _ => "?",
            }).ToArray());
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_PerHandlerRetry_RecoversOnSecondAttempt_StillMarksDelivered()
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

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_ExhaustionWritesDeadLetter_MarksDispatched_AndStopsRetrying()
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

        await dispatcher.DispatchAsync(CancellationToken.None);
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

        // Second tick must NOT re-deliver — MarkDispatched dropped the row
        // from the undelivered queue.
        await dispatcher.DispatchAsync(CancellationToken.None);
        Assert.Equal(3, attempts);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_PerHandlerIsolation_SiblingSucceedsWhenPeerExhausts()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var goodAttempts = 0;
        var badAttempts = 0;
        var good = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => goodAttempts++),
            DispatchDynamic);
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badAttempts++;
                throw new InvalidOperationException("permanent");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_iso"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(1, goodAttempts);
        Assert.Equal(2, badAttempts);
        Assert.Single(dlq.Written);
        Assert.Single(events.Marked);
    }

    [Fact]
    public async Task DispatchAsync_DeliverBeforeMarkCrash_RowStaysUndelivered_AndIsRedeliveredOnNextTick()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var seenEventIds = new List<string>();
        var processed = new HashSet<string>();
        var uniqueDeliveries = 0;
        var rec = new IdempotentRecorder(evt =>
        {
            seenEventIds.Add(evt.Id);
            if (processed.Add(evt.Id))
                uniqueDeliveries++;
        });
        var sub = new Subscription(IssueCompleted, rec, DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_crash"));
        events.ThrowOnMark = _ => true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CancellationToken.None));

        Assert.Equal(new[] { "evt_crash" }, seenEventIds);
        Assert.Equal(1, uniqueDeliveries);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);

        events.ThrowOnMark = null;
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(new[] { "evt_crash", "evt_crash" }, seenEventIds);
        Assert.Equal(1, uniqueDeliveries);
        Assert.Single(events.Marked);
        Assert.Empty(events.PendingUndelivered);
    }

    [Fact]
    public async Task DispatchAsync_MarkFailure_StopsBeforeNextEventInSameStream()
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

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_1"));
        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 2, eventId: "evt_2"));
        events.ThrowOnMark = evt => evt.Id == 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CancellationToken.None));

        Assert.Equal(["evt_1"], seen);
        Assert.Equal([1L, 2L], events.PendingUndelivered.Select(evt => evt.Id));

        events.ThrowOnMark = null;
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(["evt_1", "evt_1", "evt_2"], seen);
        Assert.Equal([1L, 2L], events.Marked.Select(mark => mark.Id));
    }

    [Fact]
    public async Task DispatchAsync_FanOutByType_MatchesClosedGenericHandler()
    {
        // Verifies the closed-generic discovery fix landed: a closed
        // ICloudEventHandler<TData> registered as a Subscription(Type=...)
        // gets invoked through the same DispatchDelegate as the
        // non-generic handlers when the CloudEventTypeMatcher matches.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();

        var received = new List<CloudEvent<IssueCompleted>>();
        var handler = new CapturingTypedHandler(received);
        // Build the closed-generic Subscription the same way
        // AddCloudEventHandlersFromAssembly would for an
        // ICloudEventHandler<TData> handler.
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

        await dispatcher.DispatchAsync(CancellationToken.None);

        var evt = Assert.Single(received);
        Assert.Equal(IssueCompleted, evt.Type);
        Assert.Equal("evt_typed", evt.Id);
        Assert.Equal("wr_1", evt.Data.WorkflowRunId);
        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_TypeMatcher_DoesNotInvokeNonMatchingSubscriptions()
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

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(1, matchedCalls);
        Assert.Equal(0, otherCalls);
    }

    [Fact]
    public async Task DispatchAsync_DeadLetterWriteFailure_KeepsRowUndelivered()
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

        var ex = await Record.ExceptionAsync(() =>
            dispatcher.DispatchAsync(CancellationToken.None));
        Assert.NotNull(ex);
        Assert.Contains("dead-letter", ex.Message);

        Assert.Equal(2, attempts);
        Assert.Empty(dlq.Written);
        Assert.Empty(events.Marked);
        Assert.Single(events.PendingUndelivered);
    }

    [Fact]
    public async Task DispatchAsync_PoisonSettlementMarkFailureCommitsNeitherSide()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore { ThrowOnMark = _ => true };
        var dlq = new FakeDeadLetterStore();
        var attempts = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempts++;
                throw new InvalidOperationException("poison");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_atomic",
            id: 1,
            eventId: "evt_atomic_failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(events.Marked);
        Assert.Empty(dlq.Written);
        Assert.Single(events.PendingUndelivered);

        events.ThrowOnMark = null;
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Single(events.Marked);
        Assert.Single(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_NoMatchingSubscription_MarksRowDeliveredWithoutInvoking()
    {
        // An event with no fan-out target is still marked DispatchedAt
        // (the dispatcher never retries it — there is nothing to retry).
        // This avoids leaving orphan undelivered rows forever.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription("test.never-matches", new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(AnyEvent, "/mohist/issues/issue_1", id: 1, eventId: "evt_orphan"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Single(events.Marked);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task DispatchAsync_DispatchedAtUsesInjectedTimeProvider()
    {
        var initial = StartTime;
        var later = initial.AddSeconds(42);
        var time = new FakeTimeProvider(initial);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_t1"));

        await dispatcher.DispatchAsync(CancellationToken.None);
        time.SetUtcNow(later);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_2", id: 2, eventId: "evt_t2"));
        await dispatcher.DispatchAsync(CancellationToken.None);

        Assert.Collection(
            events.Marked,
            m => Assert.Equal(initial, m.DispatchedAt),
            m => Assert.Equal(later, m.DispatchedAt));
    }

    [Fact]
    public async Task RedeliverAsync_LoadsDeadLetterAndReDispatchesToMatchingHandlers()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        // First attempt always throws → exhausts → DL row written.
        // Second attempt succeeds. The handler tracks per-attempt state
        // so the redelivery can observe the recovery.
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

        await dispatcher.DispatchAsync(CancellationToken.None);
        var dl = Assert.Single(dlq.Written);
        Assert.Equal(3, attempt);

        // Operator triggers redelivery — fresh dispatch from the DL row,
        // not from the (now marked) original event row.
        var result = await dispatcher.RedeliverAsync(dl.DeadLetterId, CancellationToken.None);

        Assert.Equal(4, attempt);
        Assert.True(result.Found);
        Assert.True(result.Delivered);
        Assert.Equal(1, result.Attempts);
        // The original event row's DispatchedAt is left untouched on a
        // redelivery so the dispatcher does not double-claim it.
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
        var good = new Subscription(
            IssueCompleted,
            new Recorder(_ => goodCalls++),
            DispatchDynamic);
        var bad = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                badCalls++;
                if (badStillFails)
                    throw new InvalidOperationException("poison");
            }),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [good, bad], time, handlerMaxAttempts: 2);

        events.Enqueue(FakeEventStore.Build(IssueCompleted, "/mohist/issues/issue_1", id: 1, eventId: "evt_redeliver_one"));
        await dispatcher.DispatchAsync(CancellationToken.None);
        var deadLetter = Assert.Single(dlq.Written);
        Assert.Equal(1, goodCalls);
        Assert.Equal(2, badCalls);

        badStillFails = false;
        var result = await dispatcher.RedeliverAsync(deadLetter.DeadLetterId, CancellationToken.None);

        Assert.True(result.Delivered);
        Assert.Equal(1, goodCalls);
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
        await dispatcher.DispatchAsync(CancellationToken.None);
        var deadLetter = Assert.Single(dlq.Written);

        poison = false;
        dlq.ThrowOnResolve = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.RedeliverAsync(deadLetter.DeadLetterId, CancellationToken.None));

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
    public void DispatchAsync_RejectsZeroOrNegativeBatchLimit()
    {
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);

        Assert.Throws<ArgumentOutOfRangeException>(() => new EventDispatcherService(
            events,
            [sub],
            dlq,
            time,
            Options.Create(new DispatcherOptions { BatchLimit = 0, HandlerMaxAttempts = 1 }),
            NullLogger<EventDispatcherService>.Instance));

        Assert.Throws<ArgumentOutOfRangeException>(() => new EventDispatcherService(
            events,
            [sub],
            dlq,
            time,
            Options.Create(new DispatcherOptions { BatchLimit = 10, HandlerMaxAttempts = 0 }),
            NullLogger<EventDispatcherService>.Instance));
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

    private sealed class Recorder : ICloudEventHandler
    {
        private readonly Action<CloudEvent> _onEvent;

        public Recorder(Action<CloudEvent> onEvent) => _onEvent = onEvent;

        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyRecorder : ICloudEventHandler
    {
        private readonly Action _onEvent;

        public FlakyRecorder(Action onEvent) => _onEvent = onEvent;

        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            _onEvent();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingTypedHandler : ICloudEventHandler<IssueCompleted>
    {
        private readonly List<CloudEvent<IssueCompleted>> _sink;

        public CapturingTypedHandler(List<CloudEvent<IssueCompleted>> sink) => _sink = sink;

        public bool Filter(CloudEvent<IssueCompleted> evt) => true;

        public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct)
        {
            _sink.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class IdempotentRecorder : ICloudEventHandler
    {
        private readonly Action<CloudEvent> _onEvent;

        public IdempotentRecorder(Action<CloudEvent> onEvent) => _onEvent = onEvent;

        public bool Filter(CloudEvent evt) => true;

        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }
    }
}
