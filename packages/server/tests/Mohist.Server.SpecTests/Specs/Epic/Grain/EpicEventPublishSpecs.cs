using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;
using static Mohist.Server.SpecTests.Specs.Epic.Grain.EpicEventPublishTestSupport;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

/// <summary>
/// Specs for issue-94 T-001: every epic mutation path persists its
/// domain events to <c>EpicEvents</c> through <c>IEventStore</c>, the
/// sequence id is monotonic per epic, envelope timestamps come from the
/// injected <c>TimeProvider</c>, and <c>EpicEventSerializer</c>
/// round-trips every existing variant to its reverse-DNS CloudEvents
/// type. Built directly on the in-memory SQLite <c>TestDatabase</c> +
/// a <see cref="RecordingEventStore"/> fake so the persistence path is
/// exercised end-to-end without an Orleans silo.
/// </summary>
public class EpicEventPublishSpecs
{
    private const string ProjectId = "project_1";
    private const string EpicId = "epic_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task CreateAsync_PersistsEpicCreatedEvent()
    {
        var fixedTime = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(fixedTime);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        var dto = await grain.CreateAsync(ProjectId, "Auth epic", "description", "p1");

        // CreateAsync generates a fresh epic id; the recorded event's
        // CloudEvents source uses that id, not the test constant.
        var events = await eventStore.ListEpicEventsAsync(dto.Id);
        var created = Assert.Single(events);
        Assert.Equal(EventCatalog.ReverseDns.EpicCreated, created.Envelope.Type);
        Assert.Equal(fixedTime, created.Envelope.Time);
        Assert.Equal(new Uri($"/mohist/epics/{dto.Id}", UriKind.Relative), created.Envelope.Source);
        Assert.Equal("1", created.Envelope.Subject);
        Assert.Equal(ProjectId, created.Envelope.Extensions["projectid"]);
        Assert.Equal(dto.Id, created.Envelope.Extensions["epicid"]);
        Assert.False(created.Envelope.Extensions.ContainsKey("epicno"));
        Assert.Equal("Auth epic", created.Envelope.Data!.Value.GetProperty("title").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_PersistsEpicIssueLinkedEvent()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        var linked = Assert.Single(events);
        Assert.Equal(EventCatalog.ReverseDns.EpicIssueLinked, linked.Envelope.Type);
        Assert.Equal("issue_1", linked.Envelope.Data!.Value.GetProperty("issueId").GetString());
        Assert.Equal(1, linked.Envelope.Data!.Value.GetProperty("issueNumber").GetInt32());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UnlinkIssueAsync_PersistsEpicIssueUnlinkedEvent()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        await SeedIssueAsync(database);
        await SeedLinkAsync(database, "issue_1", 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.UnlinkIssueAsync("issue_1", ProjectId);

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        var unlinked = events.FirstOrDefault(e => e.Envelope.Type == EventCatalog.ReverseDns.EpicIssueUnlinked);
        Assert.NotNull(unlinked);
        Assert.Equal("issue_1", unlinked!.Envelope.Data!.Value.GetProperty("issueId").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartAndPauseAndResume_PersistEpicStatusChangedEventsInOrder()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        // Pin an open (in-progress) linked issue so the post-Resume
        // recompute path does not auto-mark the epic done — that would
        // emit an extra EpicStatusChanged event and obscure the
        // start→pause→resume sequence the spec calls out.
        await SeedIssueAsync(database);
        await SeedLinkAsync(database, "issue_1", 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.StartAsync();
        await grain.PauseAsync("waiting on review");
        await grain.ResumeAsync();

        var statusChanges = await eventStore.ListEpicEventsAsync(EpicId);
        // The grain may have emitted extra status events (e.g. running→
        // running from a recompute, in_progress issue start) before
        // our three deliberate transitions. The spec requires that
        // every transition persists its event, not that the count is
        // exactly 3 — assert the three we issued appear in order.
        // Filter before mapping so non-status-changed events (e.g.
        // EpicStartAttemptFailed from a transient issue-start failure)
        // don't crash the property access.
        var transitions = statusChanges
            .Where(e => e.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged)
            .Select(e => (Type: e.Envelope.Type, Old: e.Envelope.Data?.GetProperty("oldStatus").GetString(), New: e.Envelope.Data?.GetProperty("newStatus").GetString()))
            .ToList();
        Assert.Contains(transitions, t => t.Old == "idle" && t.New == "running");
        var idleToRunning = transitions.FindIndex(t => t.Old == "idle" && t.New == "running");
        var runningToPaused = transitions.FindIndex(idleToRunning + 1, t => t.Old == "running" && t.New == "paused");
        var pausedToRunning = transitions.FindIndex(runningToPaused + 1, t => t.Old == "paused" && t.New == "running");
        Assert.True(idleToRunning >= 0, "missing idle→running transition");
        Assert.True(runningToPaused > idleToRunning, "missing running→paused transition");
        Assert.True(pausedToRunning > runningToPaused, "missing paused→running transition");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task Close_PersistsEpicStatusChangedAndEpicClosedEvents()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.SetStatusAsync("closed");

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        Assert.Equal(2, events.Count);
        Assert.Equal(EventCatalog.ReverseDns.EpicStatusChanged, events[0].Envelope.Type);
        Assert.Equal("closed", events[0].Envelope.Data!.Value.GetProperty("newStatus").GetString());
        Assert.Equal(EventCatalog.ReverseDns.EpicClosed, events[1].Envelope.Type);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task UpdateAsync_PersistsEpicUpdatedAndEpicPriorityChangedEvents()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database, priority: "p2");

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.UpdateAsync("New title", "New description", "p0");

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        Assert.Equal(2, events.Count);
        var priorityChanged = events.First(e => e.Envelope.Type == EventCatalog.ReverseDns.EpicPriorityChanged);
        Assert.Equal("p2", priorityChanged.Envelope.Data!.Value.GetProperty("oldPriority").GetString());
        Assert.Equal("p0", priorityChanged.Envelope.Data!.Value.GetProperty("newPriority").GetString());
        var updated = events.First(e => e.Envelope.Type == EventCatalog.ReverseDns.EpicUpdated);
        Assert.Equal("New title", updated.Envelope.Data!.Value.GetProperty("title").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task MultipleMutations_PerEpicEventIdIsMonotonic()
    {
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        // Pin an open linked issue so the post-Resume recompute path
        // does not auto-mark the epic done before we can exercise the
        // remaining transitions (Update, SetStatus "closed").
        await SeedIssueAsync(database);
        await SeedLinkAsync(database, "issue_1", 1);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.StartAsync();
        await grain.PauseAsync(null);
        await grain.ResumeAsync();
        await grain.UpdateAsync("Title 2", null, "p3");
        await grain.SetStatusAsync("closed");

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        var ids = events.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        for (var i = 1; i < ids.Count; i++)
            Assert.True(ids[i] > ids[i - 1], $"Expected monotonic Ids; got {string.Join(", ", ids)}");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EnvelopeTime_EqualsInjectedTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero);
        var (database, eventStore) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(fixedTime);
        await SeedEpicAsync(database);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", eventStore, time);
        await grain.StartAsync();
        await grain.PauseAsync("blocked");
        await grain.ResumeAsync();

        var events = await eventStore.ListEpicEventsAsync(EpicId);
        Assert.All(events, e => Assert.Equal(fixedTime, e.Envelope.Time));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ListEpicEventsAsync_EmptyForUnknownEpic()
    {
        var (_, eventStore) = CreateDatabaseWithRecordingEventStore();
        var events = await eventStore.ListEpicEventsAsync("nonexistent_epic");
        Assert.Empty(events);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EventStoreFailure_AtomicRollback_PreventsTransitionWithoutEvent()
    {
        // With atomic event persistence for status transitions, an event-store
        // append failure rolls back the entire transaction — the epic does not
        // end up in a committed state without its durable status-changed event.
        // This ensures the EpicRunningStatusHandler recovery trigger is never
        // lost relative to the state transition.
        var (database, _) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        var throwingStore = new ThrowingEventStore();

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", throwingStore, time);
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.StartAsync());

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal("idle", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ExistingSingleIssueLinkUnlinkBehaviorAndDefaultListOrderingUnchanged()
    {
        // Regression check from T-001 acceptance: single link/unlink
        // behaviour and the default EpicQuerier list ordering are not
        // affected by the new persistence wiring.
        var (database, _) = CreateDatabaseWithRecordingEventStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero));
        await SeedEpicAsync(database);
        await SeedIssueAsync(database, issueId: "issue_a", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_b", issueNumber: 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:{EpicId}", new NoopEventStore(), time);
        await grain.LinkIssueAsync("issue_a", 1, ProjectId);
        await grain.LinkIssueAsync("issue_b", 2, ProjectId);
        await grain.LinkIssueAsync("issue_a", 1, ProjectId); // idempotent
        await grain.UnlinkIssueAsync("issue_b", ProjectId);

        await using var verify = database.CreateDbContext();
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == EpicId)
            .ToListAsync();
        Assert.Single(links);
        Assert.Equal("issue_a", links[0].IssueId);
        Assert.Equal(1, links[0].IssueNumber);

        // Active-membership slot for the unlinked issue is gone; the
        // still-linked issue keeps its slot.
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == EpicId)
            .ToListAsync();
        Assert.Single(active);
        Assert.Equal("issue_a", active[0].IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void EpicEventSerializer_RoundTripsEveryExistingVariantToReverseDns()
    {
        // Each existing EpicEvent variant must map to a com.mohist.epic.*
        // string and the storage Type / Data surface must agree with the
        // concrete variant name.
        var variants = new (EpicEvent Payload, string ExpectedReverseDns, string ExpectedStorageType)[]
        {
            (new EpicCreated("Auth", "desc", "p1"), EventCatalog.ReverseDns.EpicCreated, nameof(EpicCreated)),
            (new EpicUpdated("Auth", null, "p2"), EventCatalog.ReverseDns.EpicUpdated, nameof(EpicUpdated)),
            (new EpicPriorityChanged("p1", "p2"), EventCatalog.ReverseDns.EpicPriorityChanged, nameof(EpicPriorityChanged)),
            (new EpicIssueLinked("issue_1", 1), EventCatalog.ReverseDns.EpicIssueLinked, nameof(EpicIssueLinked)),
            (new EpicIssueUnlinked("issue_1", 1), EventCatalog.ReverseDns.EpicIssueUnlinked, nameof(EpicIssueUnlinked)),
            (new EpicStatusChanged("idle", "running"), EventCatalog.ReverseDns.EpicStatusChanged, nameof(EpicStatusChanged)),
            (new EpicClosed(), EventCatalog.ReverseDns.EpicClosed, nameof(EpicClosed)),
            (new EpicReopened(), EventCatalog.ReverseDns.EpicReopened, nameof(EpicReopened)),
            (new EpicStartAttemptFailed("issue_1", 1, "transient failure"), EventCatalog.ReverseDns.EpicStartAttemptFailed, nameof(EpicStartAttemptFailed)),
        };

        foreach (var (payload, expectedReverseDns, expectedStorageType) in variants)
        {
            var busType = EpicEventSerializer.BusType(payload);
            Assert.Equal(expectedReverseDns, busType);

            var data = EpicEventSerializer.ToData(payload);
            Assert.NotEqual(JsonValueKind.Null, data.ValueKind);

            // The C# 14 union's GetType().Name returns the union
            // container ("EpicEvent"), not the underlying variant
            // type. The serializer's storage Type explicitly unwraps
            // via Unwrap(payload).GetType().Name so the value written
            // to the EpicEvents.Type column matches the variant
            // declared in the source — this is the surface that
            // produces the on-disk envelope.
            var storage = EpicEventSerializer.Type(payload);
            Assert.Equal(expectedStorageType, storage);
        }
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void EpicEventCatalog_AllReverseDnsValuesRegistered()
    {
        // The catalog.All list MUST include every EpicEvent variant's
        // reverse-DNS string — guards against silent drop-outs from
        // EventCatalog.
        var expected = new[]
        {
            EventCatalog.ReverseDns.EpicCreated,
            EventCatalog.ReverseDns.EpicUpdated,
            EventCatalog.ReverseDns.EpicPriorityChanged,
            EventCatalog.ReverseDns.EpicIssueLinked,
            EventCatalog.ReverseDns.EpicIssueUnlinked,
            EventCatalog.ReverseDns.EpicStatusChanged,
            EventCatalog.ReverseDns.EpicClosed,
            EventCatalog.ReverseDns.EpicReopened,
            EventCatalog.ReverseDns.EpicStartAttemptFailed,
        };
        foreach (var type in expected)
            Assert.Contains(type, EventCatalog.All);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task EventStore_PersistsEpicEventsViaDbRoundTrip()
    {
        // End-to-end: real EventStore writes through the migration
        // schema; ListEpicEventsAsync reads them back chronologically.
        var database = CreateDatabase();
        var epicId = "epic_db_1";
        var time = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

        var store = new EventStore(database.Factory, NullLogger<EventStore>.Instance);
        var source = EpicEventPersistence.EpicSource(epicId);

        var first = new EpicCreated("T", "d", "p2");
        var second = new EpicPriorityChanged("p2", "p0");
        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: EpicEventSerializer.BusType(first),
            time: time,
            data: EpicEventSerializer.ToData(first),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = ProjectId,
                ["epicid"] = epicId,
            }));
        await store.AppendAsync(new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: EpicEventSerializer.BusType(second),
            time: time.AddSeconds(1),
            data: EpicEventSerializer.ToData(second),
            subject: "1",
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = ProjectId,
                ["epicid"] = epicId,
            }));

        var readBack = await store.ListEpicEventsAsync(epicId);
        Assert.Equal(2, readBack.Count);
        Assert.Equal(EventCatalog.ReverseDns.EpicCreated, readBack[0].Envelope.Type);
        Assert.Equal(EventCatalog.ReverseDns.EpicPriorityChanged, readBack[1].Envelope.Type);
        Assert.True(readBack[1].Id > readBack[0].Id);

        // Source filter isolates events by epic.
        var otherEpic = await store.ListEpicEventsAsync("other_epic");
        Assert.Empty(otherEpic);

        await database.DisposeAsync();
    }

}
