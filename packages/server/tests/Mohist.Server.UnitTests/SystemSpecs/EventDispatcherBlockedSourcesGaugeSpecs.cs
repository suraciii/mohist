using System.Diagnostics.Metrics;
using Mohist.Server.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Otel;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Focused unit coverage for the <c>mohist.server.event_dispatcher.blocked_sources</c>
/// observable gauge: <see cref="EventDispatcherService"/> publishes the number
/// of streams parked on their lease with a pending retry, without any
/// high-cardinality source identifier tags. A parked stream is one whose head
/// failed and whose next attempt time has not elapsed; other streams keep
/// draining.
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
            new FakeDispatchStreamLeaseStore(),
            time,
            Options.Create(new EventDispatcherOptions
            {
                MaxAttempts = handlerMaxAttempts,
                BaseBackoff = baseBackoff ?? TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(30),
            }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
    }

    private static MeterListener Listen(Meter meter, Action<long> observe)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == meter)
                    current.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == RuntimeMetricCatalog.EventDispatcherBlockedSources)
                observe(value);
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task ParkedStream_PublishesPositiveCount_HeadFailureBlocksOnlyItsStream()
    {
        // One handler always throws; the head row parks its stream on the
        // lease with backoff. The later row in the same stream is not
        // delivered this pass (FIFO head-of-line), the handler is invoked
        // once, and the gauge reports one parked stream.
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

        var observed = new List<long>();
        using var listener = Listen(dispatcher.Meter, observed.Add);

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

        await dispatcher.DrainAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        Assert.Equal(1, calls);
        Assert.Single(observed);
        Assert.Equal(1L, observed[0]);
    }

    [Fact]
    public async Task NoParkedStream_PublishesZero_AfterRecoveryPass()
    {
        // The gauge reports parked-lease counts refreshed after each claimed
        // pass. The first pass parks the stream until backoff elapses. After
        // the backoff passes and the retry settles the rows, the lease is
        // released and the gauge reports zero.
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

        var observed = new List<long>();
        using var listener = Listen(dispatcher.Meter, observed.Add);

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

        await dispatcher.DrainAsync(CancellationToken.None);
        listener.RecordObservableInstruments();
        // Pass one: head row throws → stream parked for backoff.
        Assert.Equal(1L, observed[0]);

        time.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.DrainAsync(CancellationToken.None);
        listener.RecordObservableInstruments();

        // Pass two: the parked retry is claimable again, the handler now
        // succeeds, both rows settle, and the lease is released.
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

        var samples = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter == dispatcher.Meter)
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

        await dispatcher.DrainAsync(CancellationToken.None);
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
