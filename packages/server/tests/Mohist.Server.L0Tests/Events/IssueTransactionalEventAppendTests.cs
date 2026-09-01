using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.TestSupport;
using Mohist.Server.L0Tests.Support;
using Orleans;
using Xunit;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.L0Tests.Events;

public class IssueTransactionalEventAppendTests : IAsyncLifetime
{
    private const string ProjectId = "proj_issue_txn";
    private readonly TestSqliteDatabase _database;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private EventStore _eventStore = null!;

    public IssueTransactionalEventAppendTests()
    {
        _database = TestSqliteDatabase.CreateModelSchema();
        _dbFactory = new TestDbContextFactory(_database.Options);
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsIssueStateAndOwnEventsTogether()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(1);

        await store.SaveAsync(Key(1), issue, [
            new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
            new IssueArchived(),
        ]);

        var events = await _eventStore.ListIssueEventsAsync(ProjectId, 1);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueCreated);
        Assert.Contains(events, entry => entry.Envelope.Type == EventCatalog.ReverseDns.IssueArchived);

        var loaded = await store.LoadAsync(Key(1));
        Assert.NotNull(loaded);
        Assert.Equal(ProjectId, loaded!.ProjectId);
        Assert.Equal(1, loaded.Number);
    }

    [Fact]
    public async Task SaveAsync_EventWriteFailureRollsBackIssueStateAndEvents()
    {
        var store = CreateStore(new ThrowingEventStore());
        var issue = BuildIssue(2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(Key(2), issue, [
                new IssueCreated("Hello", "p2", new Dictionary<string, string>(), null, null),
                new IssueArchived(),
            ]));

        Assert.Contains("event write failed", exception.Message);
        await using var verify = new MohistDbContext(_database.Options);
        Assert.Empty(await verify.Issues.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.IssueEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_PersistsScopedEventSourceAcrossDbContexts()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(3);

        await store.SaveAsync(Key(3), issue, [new IssueArchived()]);

        await using var fresh = new MohistDbContext(_database.Options);
        var row = Assert.Single(await fresh.IssueEvents.AsNoTracking().ToListAsync());
        Assert.Equal($"/mohist/projects/{ProjectId}/issues/3", row.Source);
        Assert.Equal(EventCatalog.ReverseDns.IssueArchived, row.Type);
        Assert.Null(row.DispatchedAt);
        var state = Assert.Single(await fresh.Issues.AsNoTracking().ToListAsync());
        Assert.Equal(ProjectId, state.ProjectId);
        Assert.Equal(3, state.Number);
    }

    [Fact]
    public async Task SaveAsync_StampsOnlyIssueOwnedLineage()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(4, epicNumber: 7);

        await store.SaveAsync(Key(4), issue, [new IssueArchived()]);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync(ProjectId, 4));
        Assert.Equal(ProjectId, stored.Envelope.Extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("4", stored.Envelope.Extensions[EventCatalog.Lineage.Issue]);
        Assert.Equal("7", stored.Envelope.Extensions[EventCatalog.Lineage.Epic]);
    }

    [Fact]
    public async Task SaveAsync_AllIssueEventVariants_SatisfyIssueProducerFamily()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(8, epicNumber: 7);
        IssueEvent[] events =
        [
            new IssueCreated("Created", "p2", new Dictionary<string, string>(), null, null),
            new IssueLabelsChanged(new Dictionary<string, string>(), new Dictionary<string, string>()),
            new IssuePriorityChanged("p2", "p1"),
            new IssueDraftChanged(false, true),
            new IssuePrerequisiteAdded(9),
            new IssuePrerequisiteRemoved(9),
            new IssueWorkflowProfileChanged(null),
            new IssueEpicChanged(6, 7),
            new IssueWorkStarted("workflow_8"),
            new IssueCompleted("workflow_8"),
            new IssueCancelled(null),
            new IssueArchived(),
            new IssueUnarchived(),
            new IssueReopened(),
        ];

        await store.SaveAsync(Key(8), issue, events);

        var stored = await _eventStore.ListIssueEventsAsync(ProjectId, 8);
        Assert.Equal(events.Length, stored.Count);
        for (var i = 0; i < events.Length; i++)
        {
            ProducerConformance.Assert(
                EventProducerFamily.Issue,
                stored[i].Envelope.Extensions,
                new(ProjectId: ProjectId, Issue: "8", Epic: "7"));
        }
    }

    [Fact]
    public async Task SaveAsync_AffiliationChangePersistsIssueStateAndOwnEventTogether()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(7, epicNumber: 7);
        issue.AssignEpic(9);
        var events = issue.PendingEvents.ToList();

        await store.SaveAsync(Key(7), issue, events);

        var loaded = await store.LoadAsync(Key(7));
        Assert.NotNull(loaded);
        Assert.Equal(9, loaded!.EpicNumber);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync(ProjectId, 7));
        Assert.Equal(EventCatalog.ReverseDns.IssueEpicChanged, stored.Envelope.Type);
        Assert.Equal("9", stored.Envelope.Extensions[EventCatalog.Lineage.Epic]);

        await using var verify = new MohistDbContext(_database.Options);
        Assert.Empty(await verify.Epics.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.WorkflowRuns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void IssueLineage_OmitsEpicWhenIssueHasNoEpicNumber()
    {
        var extensions = IssueLineage.BuildExtensions(BuildIssue(5));

        Assert.Equal(ProjectId, extensions[EventCatalog.Lineage.ProjectId]);
        Assert.Equal("5", extensions[EventCatalog.Lineage.Issue]);
        Assert.False(extensions.ContainsKey(EventCatalog.Lineage.Epic));
    }

    [Fact]
    public async Task SaveAsync_UnaffiliatedIssue_SatisfiesIssueProducerFamilyWithoutEpic()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(9);

        await store.SaveAsync(Key(9), issue, [new IssueArchived()]);

        var stored = Assert.Single(await _eventStore.ListIssueEventsAsync(ProjectId, 9));
        ProducerConformance.Assert(
            EventProducerFamily.Issue,
            stored.Envelope.Extensions,
            new(ProjectId: ProjectId, Issue: "9"));
    }

    [Fact]
    public async Task SaveAsync_WithoutEventsStillPersistsIssueState()
    {
        var store = CreateStore(_eventStore);
        var issue = BuildIssue(6);

        await store.SaveAsync(Key(6), issue);

        Assert.NotNull(await store.LoadAsync(Key(6)));
        Assert.Empty(await _eventStore.ListIssueEventsAsync(ProjectId, 6));
    }

    private IssueStore CreateStore(IEventStore events) =>
        new(_dbFactory, events, NullLogger<IssueStore>.Instance, new EventDispatchSignal());

    private static string Key(int number) => GrainKey.Issue(new IssueKey(ProjectId, number));

    private static DomainIssue BuildIssue(int number, int? epicNumber = null) => new()
    {
        ProjectId = ProjectId,
        Number = number,
        Title = "Transaction probe",
        Priority = "p2",
        EpicNumber = epicNumber,
    };



    private sealed class ThrowingEventStore : IEventStore
    {
        private int _callCount;

        public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

        public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount >= 2) throw new InvalidOperationException("simulated event write failed");
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

        public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
        public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
        public Task<IReadOnlyList<PendingStream>> ListPendingStreamsAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingStream>>([]);
        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredByStreamAsync(EventOrigin origin, string source, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
        public Task MarkDispatchedRangeAsync(EventOrigin origin, string source, IReadOnlyList<long> ids, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;
    }
}
