using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.SpecTests.Support;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// Issue producer. Covers issue-361 T-004 scenarios: issue state and
/// emitted IssueEvents commit atomically; an event-row write failure
/// rolls back the state transaction and propagates (no
/// log-and-swallow catch remains); durable event rows survive a
/// crash-after-commit and remain readable on a fresh
/// <c>DbContext</c>. Also covers issue-412 T-003 lineage stamping:
/// events stamped by <c>IssueStore.SaveAsync</c> carry the issue's
/// identity (<c>projectid</c>, <c>issueid</c>, <c>issue</c>) in
/// <c>extensions</c> (D3 — user-visible number uses the protocol
/// <c>issue</c> key, no <c>issueno</c> key remains), additionally
/// stamp <c>epicid</c> when <c>state.EpicId</c> is non-null (D5 — no
/// cross-aggregate query), and every emitted envelope satisfies the
/// catalog's declared required lineage attributes via the conformance
/// helper.
/// </summary>
[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class IssueTransactionalEventAppendSpecs : IAsyncLifetime
{
    private const string ProjectId = "proj_issue_txn";
    private const string IssueId = "issue_txn_1";
    private const int IssueNumber = 7;

    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private readonly NullDispatchGrainFactory _grainFactory = new();
    private EventStore _eventStore = null!;

    public IssueTransactionalEventAppendSpecs()
    {
        var connectionString = $"Data Source=issue-transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbFactory = new Factory(_options);

        MigratedSqliteTemplate.CopyTo(_keeper);
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndIssueEventRowsTogether()
    {
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_ok", number: 1);

        await store.SaveAsync(issue.Id, issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var stored = await _eventStore.ListIssueEventsAsync("issue_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.issue.created");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.issue.archived");

        var loaded = await store.LoadAsync("issue_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("issue_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        var store = new IssueStore(_dbFactory, new ThrowingEventStore(), _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_fail", number: 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(issue.Id, issue, [
                new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
                new IssueArchived(),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.Issues.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.IssueEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_IssueEventRowsRemainDurableOnFreshDbContext()
    {
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_crash", number: 3);

        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.IssueEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/issues/issue_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("com.mohist.issue.archived", row.Type);
        Assert.Null(row.DispatchedAt);

        var issueRow = Assert.Single(await freshDb.Issues.AsNoTracking()
            .Where(r => r.IssueId == "issue_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(issueRow.State));
    }

    [Fact]
    public async Task SaveAsync_StampsProjectIdIssueIdAndIssueOnExtensions()
    {
        // Identity stamping at write time: every issue.* event carries
        // the unified protocol key `issue` (replacing the legacy
        // `issueno` — D3) alongside `projectid` / `issueid`. No
        // `issueno` key is present. Epic id is absent because the
        // test fixture issue has no EpicId.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue(IssueId, number: 4);

        await store.SaveAsync(issue.Id, issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var stored = await _eventStore.ListIssueEventsAsync(IssueId);
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
            Assert.Equal(ProjectId, stampedProjectId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
            Assert.Equal(IssueId, stampedIssueId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issue", out var stampedIssue));
            Assert.Equal("4", stampedIssue);
            Assert.False(entry.Envelope.Extensions.ContainsKey("issueno"));
            Assert.False(entry.Envelope.Extensions.ContainsKey("epicid"));
            Assert.Equal("4", entry.Envelope.Subject);
        }
    }

    [Fact]
    public async Task SaveAsync_IssueWithEpicAffiliation_StampsEpicIdOnExtensions()
    {
        // D5 denormalization: when the issue's own state carries an
        // EpicId (written by the Epic domain at link time, T-004),
        // every issue.* event stamps `epicid` from that state. No
        // cross-aggregate query is issued; the issue aggregate's
        // own state is the sole source.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_with_epic", epicId: "epic_txn_1", number: 5);

        await store.SaveAsync(issue.Id, issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var stored = await _eventStore.ListIssueEventsAsync("issue_txn_with_epic");
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
            Assert.Equal(ProjectId, stampedProjectId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
            Assert.Equal("issue_txn_with_epic", stampedIssueId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issue", out var stampedIssue));
            Assert.Equal("5", stampedIssue);
            Assert.True(entry.Envelope.Extensions.TryGetValue("epicid", out var stampedEpicId));
            Assert.Equal("epic_txn_1", stampedEpicId);
            Assert.False(entry.Envelope.Extensions.ContainsKey("issueno"));
        }
    }

    [Fact]
    public async Task SaveAsync_IssueWithoutEpicAffiliation_OmitsEpicIdEntirely()
    {
        // Absent affiliation is omitted, never an empty value (the
        // protocol contract). When state.EpicId is null, no `epicid`
        // key is present on the envelope.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_no_epic", epicId: null, number: 6);

        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync("issue_txn_no_epic"));
        Assert.False(stored.Envelope.Extensions.ContainsKey("epicid"));
        Assert.Contains("issue", stored.Envelope.Extensions.Keys);
        Assert.Contains("projectid", stored.Envelope.Extensions.Keys);
        Assert.Contains("issueid", stored.Envelope.Extensions.Keys);
    }

    [Fact]
    public async Task SaveAsync_AffiliationStagedBeforeEvent_UsesCommittedLinkAndUnlinkSnapshots()
    {
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_snapshot", number: 17);

        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        await StageEpicAffiliationAsync(issue.Id, "epic_snapshot");
        await store.SaveAsync(issue.Id, issue, [new IssueReopened()]);

        await StageEpicAffiliationAsync(issue.Id, null);
        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        var stored = await _eventStore.ListIssueEventsAsync(issue.Id);
        Assert.Equal(3, stored.Count);
        Assert.False(stored[0].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));
        Assert.Equal("epic_snapshot", stored[1].Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(stored[2].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));
    }

    [Fact]
    public async Task StageEpicAffiliation_RejectsStaleWritersAndReappliesFromCurrentIssueState()
    {
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_atomic_affiliation", number: 18);
        await store.SaveAsync(issue.Id, issue);

        await using var link = await _dbFactory.CreateDbContextAsync();
        await IssueStore.StageEpicAffiliationAsync(link, issue.Id, "epic_atomic");

        var afterLinkRead = (await store.LoadAsync(issue.Id))!;
        afterLinkRead.Update("Changed after link snapshot read", null, null, null,
            new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc));
        await store.SaveAsync(afterLinkRead.Id, afterLinkRead);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => link.SaveChangesAsync());

        await StageEpicAffiliationAsync(issue.Id, "epic_atomic");

        var linked = (await store.LoadAsync(issue.Id))!;
        Assert.Equal("Changed after link snapshot read", linked.Title);
        Assert.Equal("epic_atomic", linked.EpicId);
        await store.SaveAsync(linked.Id, linked, [new IssueArchived()]);

        await using var unlink = await _dbFactory.CreateDbContextAsync();
        await IssueStore.StageEpicAffiliationAsync(unlink, issue.Id, null);

        var afterUnlinkRead = (await store.LoadAsync(issue.Id))!;
        afterUnlinkRead.Update("Changed after unlink snapshot read", null, null, null,
            new DateTime(2026, 7, 15, 0, 1, 0, DateTimeKind.Utc));
        await store.SaveAsync(afterUnlinkRead.Id, afterUnlinkRead);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => unlink.SaveChangesAsync());

        await StageEpicAffiliationAsync(issue.Id, null);

        var unlinked = (await store.LoadAsync(issue.Id))!;
        Assert.Equal("Changed after unlink snapshot read", unlinked.Title);
        Assert.Null(unlinked.EpicId);
        await store.SaveAsync(unlinked.Id, unlinked, [new IssueReopened()]);

        var stored = await _eventStore.ListIssueEventsAsync(issue.Id);
        Assert.Equal("epic_atomic", stored[0].Envelope.Extensions[EventCatalog.Lineage.EpicId]);
        Assert.False(stored[1].Envelope.Extensions.ContainsKey(EventCatalog.Lineage.EpicId));
    }

    private async Task StageEpicAffiliationAsync(string issueId, string? epicId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await IssueStore.StageEpicAffiliationAsync(
            db,
            issueId,
            epicId);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SaveAsync_IssueWithoutEpicAffiliation_OmitsEpicId_AndEmptyStringEpicIdIsTreatedAsAbsent()
    {
        // Defensive: a whitespace-only EpicId in state is normalized to
        // null by the property setter and therefore omits `epicid`
        // from the stamped envelope.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_empty_epic", epicId: "   ", number: 8);

        await store.SaveAsync(issue.Id, issue, [new IssueArchived()]);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync("issue_txn_empty_epic"));
        Assert.Null(issue.EpicId);
        Assert.False(stored.Envelope.Extensions.ContainsKey("epicid"));
    }

    [Fact]
    public async Task SaveAsync_StampedEnvelopes_SatisfyEventCatalogRequiredAttributes()
    {
        // Conformance check (D8): every emitted envelope satisfies
        // the catalog's declared required lineage attributes for
        // its type — `issue.*` requires {projectid, issueid, issue}.
        // Drives an issue with and without an epic affiliation.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);

        var affiliatedIssue = BuildIssue("issue_txn_conformance_with_epic", epicId: "epic_txn_conformance", number: 11);
        await store.SaveAsync(affiliatedIssue.Id, affiliatedIssue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var unaffiliatedIssue = BuildIssue("issue_txn_conformance_no_epic", epicId: null, number: 12);
        await store.SaveAsync(unaffiliatedIssue.Id, unaffiliatedIssue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueReopened(),
        ]);

        var affiliated = await _eventStore.ListIssueEventsAsync("issue_txn_conformance_with_epic");
        var unaffiliated = await _eventStore.ListIssueEventsAsync("issue_txn_conformance_no_epic");
        Assert.Equal(2, affiliated.Count);
        Assert.Equal(2, unaffiliated.Count);

        foreach (var entry in affiliated.Concat(unaffiliated))
        {
            // Throws when an attribute is missing or empty.
            EnvelopeConformance.AssertRequired(entry.Envelope);
            var missing = EnvelopeConformance.Missing(entry.Envelope);
            Assert.Empty(missing);
        }
    }

    [Fact]
    public void IssueLineage_BuildExtensions_StampsProjectIdIssueIdIssueAndOptionalEpicId()
    {
        // The pure helper is what the store's transactional save calls.
        // Stamping must read ONLY from the supplied issue state — no
        // hidden database calls — so we exercise it with a
        // hand-constructed state and confirm every lineage key.
        var affiliated = BuildIssue("issue_lineage_with_epic", epicId: "epic_lineage_1", number: 15);
        var extensions = IssueLineage.BuildExtensions(affiliated);
        Assert.Equal(ProjectId, extensions["projectid"]);
        Assert.Equal("issue_lineage_with_epic", extensions["issueid"]);
        Assert.Equal("15", extensions["issue"]);
        Assert.Equal("epic_lineage_1", extensions["epicid"]);
        Assert.False(extensions.ContainsKey("issueno"));

        var unaffiliated = BuildIssue("issue_lineage_no_epic", epicId: null, number: 16);
        var noEpicExtensions = IssueLineage.BuildExtensions(unaffiliated);
        Assert.Equal(ProjectId, noEpicExtensions["projectid"]);
        Assert.Equal("issue_lineage_no_epic", noEpicExtensions["issueid"]);
        Assert.Equal("16", noEpicExtensions["issue"]);
        Assert.False(noEpicExtensions.ContainsKey("epicid"));
        Assert.False(noEpicExtensions.ContainsKey("issueno"));
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_state_only", number: 13);

        await store.SaveAsync(issue.Id, issue, []);

        var loaded = await store.LoadAsync("issue_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListIssueEventsAsync("issue_txn_state_only"));
    }

    [Fact]
    public async Task SaveAsync_NoEventsOverload_RemainsForCallerWithNoPendingEvents()
    {
        // The grain takes the events-aware overload when there are
        // pending events, but the no-events overload of SaveAsync must
        // still exist for callers that haven't recorded any domain
        // events (e.g. an issue that has been touched but did not
        // transition). It continues to commit the state row cleanly.
        var store = new IssueStore(_dbFactory, _eventStore, _grainFactory, NullLogger<IssueStore>.Instance);
        var issue = BuildIssue("issue_txn_no_events_path", number: 14);

        await store.SaveAsync(issue.Id, issue);

        var loaded = await store.LoadAsync("issue_txn_no_events_path");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListIssueEventsAsync("issue_txn_no_events_path"));
    }

    private static DomainIssue BuildIssue(string id, string? epicId = null, int? number = null)
    {
        return new DomainIssue
        {
            Id = id,
            ProjectId = ProjectId,
            Number = number ?? IssueNumber,
            Title = "Transaction probe",
            Priority = "p2",
            EpicId = epicId,
        };
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Minimal <see cref="IGrainFactory"/> stand-in for transactional
    /// unit specs. The dispatcher is a no-op grain reference; producers
    /// only need to call DispatchNowAsync without exceptions. Lets the
    /// store exercise its post-commit poke code path without spinning up
    /// an Orleans silo.
    /// </summary>
    private sealed class NullDispatchGrainFactory : IGrainFactory
    {
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix)
        {
            if (typeof(TGrainInterface) == typeof(IEventDispatcherGrain))
                return (TGrainInterface)(object)new NullEventDispatcherGrain();
            throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");
        }

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix)
            => throw new NotSupportedException($"NullDispatchGrainFactory does not support {typeof(TGrainInterface).Name}");

        TGrainObserverInterface IGrainFactory.CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        void IGrainFactory.DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        IGrain IGrainFactory.GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        IAddressable IGrainFactory.GetGrain(Type interfaceType, IdSpan grainKey)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Drop-in <see cref="IEventDispatcherGrain"/> reference whose
    /// <see cref="DispatchNowAsync"/> returns <see cref="Task.CompletedTask"/>.
    /// Lets the post-commit poke fire without an Orleans silo.
    /// </summary>
    private sealed class NullEventDispatcherGrain : IGrainWithStringKey, IEventDispatcherGrain
    {
        public Task DispatchNowAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<DeadLetterRedeliveryResult> RedeliverAsync(long deadLetterId, CancellationToken ct = default) =>
            Task.FromResult(new DeadLetterRedeliveryResult(false, false, 0, "null grain"));

        public Task ReceiveReminder(string reminderName, TickStatus status) => Task.CompletedTask;

        public GrainId GrainId => default;
        public string Key => string.Empty;
    }

    /// <summary>
    /// <see cref="IEventStore"/> that throws on the second append in a
    /// save transaction, simulating an event-row write failure (e.g.
    /// constraint violation). Used to verify that the store does NOT
    /// swallow the exception and that the issue state transaction is
    /// rolled back. Mirrors the WorkflowRunStore equivalent from T-003.
    /// </summary>
    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2)
            {
                throw new InvalidOperationException("simulated event write failed");
            }
            await db.IssueEvents.AddAsync(new IssueEventRow
            {
                Id = _callCount,
                Source = envelope.Source.ToString(),
                EventId = envelope.Id,
                Type = envelope.Type,
                Time = envelope.Time,
                SpecVersion = envelope.SpecVersion,
                Subject = envelope.Subject,
                DataContentType = envelope.DataContentType ?? "application/json",
                Data = envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson = "{}",
            }, ct);
        }

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
