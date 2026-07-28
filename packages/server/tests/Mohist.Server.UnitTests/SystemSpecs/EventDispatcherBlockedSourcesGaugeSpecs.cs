using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Otel;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Focused unit coverage for the T-003 <c>mohist.server.event_dispatcher.blocked_sources</c>
/// observable gauge: <see cref="EventDispatcherService"/> publishes the count of
/// sources blocked by a pending handler retry in the most recent dispatch cycle,
/// without any high-cardinality source identifier tags. The dispatcher service
/// owns its own meter so the singleton lifetime lines up with the reminder
/// grain. Spec: <c>openspec/changes/issue-502/specs/event-dispatcher/spec.md
/// #Blocked-source-count-is-observable</c>.
/// </summary>
public class EventDispatcherBlockedSourcesGaugeSpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string IssueCompleted = EventCatalog.ReverseDns.IssueCompleted;

    private static EventDispatcherService BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subs,
        FakeTimeProvider time,
        int handlerMaxAttempts = 3,
        TimeSpan? baseBackoff = null)
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
                MaxBackoff = TimeSpan.FromSeconds(30),
            }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
    }

    [Fact]
    public async Task PendingRetry_PublishesPositiveBlockedSourceCount_AndSkipsLaterRowsInSameSource()
    {
        // One handler always throws; the first row for the same source consumes
        // its retry budget and the dispatcher's blockedSources set records the
        // source as blocked. The later row from the same source is skipped in
        // this cycle, the handler is not re-invoked, and the gauge reports a
        // positive count for the completed cycle.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var calls = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                calls++;
                throw new InvalidOperationException("transient");
            }),
            DispatchDynamic);
        using var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 3);
        var dispatcherMeter = dispatcher.Meter;

        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == dispatcherMeter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == RuntimeMetricCatalog.EventDispatcherBlockedSources)
                observed.Add(value);
        });
        listener.Start();

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_blocked",
            id: 1,
            eventId: "evt_blocked_1"));
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_blocked",
            id: 2,
            eventId: "evt_blocked_2"));

        await dispatcher.DispatchAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        // The pending retry blocked the source; the later row was not delivered
        // this cycle. The single call into the handler is the only one for the
        // blocked source — the FIFO skip must not re-invoke the handler.
        Assert.Equal(1, calls);
        Assert.Single(observed);
        Assert.Equal(1L, observed[0]);
    }

    [Fact]
    public async Task NoBlockedSource_PublishesZero_AfterRecoveryCycle()
    {
        // The blocked-source gauge reports the last completed cycle's outcome.
        // The first cycle blocks the source because a handler is awaiting its
        // next retry time. After the backoff elapses and the next cycle settles
        // the row successfully, the source is no longer blocked and the gauge
        // reports zero.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var attempt = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                attempt++;
                if (attempt < 2)
                    throw new InvalidOperationException("transient");
            }),
            DispatchDynamic);
        using var dispatcher = BuildDispatcher(
            events,
            dlq,
            [sub],
            time,
            handlerMaxAttempts: 3,
            baseBackoff: TimeSpan.FromSeconds(1));
        var dispatcherMeter = dispatcher.Meter;

        var observed = new List<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == dispatcherMeter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == RuntimeMetricCatalog.EventDispatcherBlockedSources)
                observed.Add(value);
        });
        listener.Start();

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_recover",
            id: 1,
            eventId: "evt_recover_1"));
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_recover",
            id: 2,
            eventId: "evt_recover_2"));

        await dispatcher.DispatchAsync(CancellationToken.None);
        listener.RecordObservableInstruments();
        // Cycle one: first row throws → pending retry → source is blocked.
        // The second row is skipped under FIFO blocking.
        Assert.Equal(1L, observed[0]);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DispatchAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        // Cycle two: the retry elapses, the handler now succeeds, the source
        // is settled, the second row is delivered in the same cycle. The
        // completed cycle has no source whose earlier event is awaiting retry,
        // so the gauge reports zero.
        Assert.Equal(2, observed.Count);
        Assert.Equal(0L, observed[1]);
    }

    [Fact]
    public async Task Gauge_PublishesNoSourceTags_OrOtherHighCardinalityAttributes()
    {
        // Spec contract: the gauge reports a count only and never tags
        // observations with a source identifier or any other unbounded key.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var calls = 0;
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() =>
            {
                calls++;
                throw new InvalidOperationException("transient");
            }),
            DispatchDynamic);
        using var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 5);
        var dispatcherMeter = dispatcher.Meter;

        var samples = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == dispatcherMeter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == RuntimeMetricCatalog.EventDispatcherBlockedSources)
                samples.Add(tags.ToArray());
        });
        listener.Start();

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_tags",
            id: 1,
            eventId: "evt_tags_1"));
        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_tags",
            id: 2,
            eventId: "evt_tags_2"));

        await dispatcher.DispatchAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        Assert.Equal(1, calls);
        Assert.NotEmpty(samples);
        var tags = Assert.Single(samples);
        Assert.Empty(tags);
    }

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }
}
