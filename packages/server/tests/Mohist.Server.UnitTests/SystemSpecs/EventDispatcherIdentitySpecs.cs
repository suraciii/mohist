using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

/// <summary>
/// Unit specs for the durable subscription identity added in
/// <see cref="EventDispatcherService"/> (issue-493 T-003). These tests
/// prove the explicit identity option is honored at registration, written
/// into the dead-letter row, and matched back to the relocated handler
/// during operator-triggered redelivery, while the default identity
/// (runtime full type name) keeps working for handlers that never move.
/// Spec: <c>openspec/changes/issue-493/specs/server-architecture-alignment/spec.md#Domain-owned-durable-reactions</c>.
/// </summary>
public class EventDispatcherIdentitySpecs
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private const string IssueCompleted = EventCatalog.ReverseDns.IssueCompleted;

    private static EventDispatcherService BuildDispatcher(
        FakeEventStore events,
        FakeDeadLetterStore deadLetters,
        IEnumerable<Subscription> subs,
        FakeTimeProvider time,
        int handlerMaxAttempts = 3)
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
                BaseBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(30),
            }),
            NullLogger<EventDispatcherService>.Instance,
            NullEventPushQueue.Instance);
    }

    [Fact]
    public async Task BuildDeadLetter_WritesSubscriptionIdentity_NotRuntimeTypeFullName()
    {
        // Subscription declares a pre-relocation durable identity. The dead
        // letter row must store that identity verbatim, so a subsequent
        // redelivery can resolve the handler even after the handler class
        // moves to a new namespace (T-003 regression).
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        const string durableIdentity = "Mohist.Server.Events.Subscriptions.PreservedIdentity";
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => throw new InvalidOperationException("poison")),
            DispatchDynamic,
            durableIdentity);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_legacy_id",
            id: 1,
            eventId: "evt_legacy_id"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        var dl = Assert.Single(dlq.Written);
        Assert.Equal(durableIdentity, dl.FailingHandler);
        Assert.NotEqual(typeof(FlakyRecorder).FullName, dl.FailingHandler);
    }

    [Fact]
    public async Task BuildDeadLetter_DefaultsToRuntimeFullName_WhenIdentityOmitted()
    {
        // The default identity contract is unchanged: a handler without an
        // explicit SubscriptionAttribute.Identity still writes its runtime
        // full type name into the dead-letter row, so historical handlers
        // resolve the same way as before T-003.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(
            IssueCompleted,
            new FlakyRecorder(() => throw new InvalidOperationException("poison")),
            DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time, handlerMaxAttempts: 1);

        events.Enqueue(FakeEventStore.Build(
            IssueCompleted,
            "/mohist/issues/issue_default_id",
            id: 1,
            eventId: "evt_default_id"));

        await dispatcher.DispatchAsync(CancellationToken.None);

        var dl = Assert.Single(dlq.Written);
        Assert.Equal(typeof(FlakyRecorder).FullName, dl.FailingHandler);
    }

    [Fact]
    public async Task RedeliverAsync_MatchesExplicitPreRelocationIdentity()
    {
        // Regression for issue-493 T-003: a dead-letter row persisted before
        // a handler namespace move must still resolve to the relocated
        // handler when the moved handler declares its pre-relocation full
        // name as its durable identity.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        const string legacyIdentity =
            "Mohist.Server.Events.Subscriptions.IssueWorkflowCompletionHandler";
        var calls = 0;
        var relocatedHandler = new Recorder(_ => calls++);
        var sub = new Subscription(
            IssueCompleted,
            relocatedHandler,
            DispatchDynamic,
            legacyIdentity);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        var row = new DeadLetterRow
        {
            DeadLetterId = 71,
            Origin = nameof(EventOrigin.Issue),
            Id = 7,
            Source = "/mohist/issues/issue_relocated",
            EventId = "evt_relocated",
            Type = IssueCompleted,
            Time = time.GetUtcNow(),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
            ExtensionsJson = "{}",
            FailingHandler = legacyIdentity,
            ErrorMessage = "stale pre-relocation row",
            ErrorStack = "stack",
            AttemptCount = 1,
            DeadLetteredAt = time.GetUtcNow(),
        };
        await dlq.WriteAsync(row);

        var result = await dispatcher.RedeliverAsync(row.DeadLetterId, CancellationToken.None);

        Assert.True(result.Found);
        Assert.True(result.Delivered);
        Assert.Equal(1, calls);
        Assert.Empty(dlq.Written);
    }

    [Fact]
    public async Task RedeliverAsync_StillMatchesCurrentIdentity_ForRowsWrittenAfterMove()
    {
        // A new dead-letter row written after relocation should also be
        // redeliverable: the persisted identity stays equal to the durable
        // identity declared on the subscription.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        const string durableIdentity = "Mohist.Server.Issue.WorkflowCompletionHandler";
        var calls = 0;
        var relocatedHandler = new Recorder(_ => calls++);
        var sub = new Subscription(
            IssueCompleted,
            relocatedHandler,
            DispatchDynamic,
            durableIdentity);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        var row = new DeadLetterRow
        {
            DeadLetterId = 72,
            Origin = nameof(EventOrigin.Issue),
            Id = 11,
            Source = "/mohist/issues/issue_post_relocation",
            EventId = "evt_post_relocation",
            Type = IssueCompleted,
            Time = time.GetUtcNow(),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
            ExtensionsJson = "{}",
            FailingHandler = durableIdentity,
            ErrorMessage = "new failure after relocation",
            ErrorStack = "stack",
            AttemptCount = 1,
            DeadLetteredAt = time.GetUtcNow(),
        };
        await dlq.WriteAsync(row);

        var result = await dispatcher.RedeliverAsync(row.DeadLetterId, CancellationToken.None);

        Assert.True(result.Found);
        Assert.True(result.Delivered);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RedeliverAsync_NoSubscriptionMatchingLegacyIdentity_ReturnsNotRegistered()
    {
        // Without a subscription declaring the legacy identity as its
        // durable identity, redelivery must report the handler is not
        // registered for the event type — preserving the existing failure
        // surface and preventing accidental dispatch to an unrelated
        // handler that happens to live at a renamed namespace.
        var time = new FakeTimeProvider(StartTime);
        var events = new FakeEventStore();
        var dlq = new FakeDeadLetterStore();
        var sub = new Subscription(IssueCompleted, new Recorder(_ => { }), DispatchDynamic);
        var dispatcher = BuildDispatcher(events, dlq, [sub], time);

        var row = new DeadLetterRow
        {
            DeadLetterId = 73,
            Origin = nameof(EventOrigin.Issue),
            Id = 13,
            Source = "/mohist/issues/issue_orphan",
            EventId = "evt_orphan",
            Type = IssueCompleted,
            Time = time.GetUtcNow(),
            SpecVersion = "1.0",
            DataContentType = "application/json",
            Data = JsonDocument.Parse("{}").RootElement.Clone(),
            ExtensionsJson = "{}",
            FailingHandler = "Mohist.Server.Events.Subscriptions.OldName",
            ErrorMessage = "stale pre-relocation row",
            ErrorStack = "stack",
            AttemptCount = 1,
            DeadLetteredAt = time.GetUtcNow(),
        };
        await dlq.WriteAsync(row);

        var result = await dispatcher.RedeliverAsync(row.DeadLetterId, CancellationToken.None);

        Assert.True(result.Found);
        Assert.False(result.Delivered);
        Assert.Contains("OldName", result.Error, StringComparison.Ordinal);
        Assert.Contains("not registered", result.Error, StringComparison.Ordinal);
    }

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }
}