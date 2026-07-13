using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Events;

/// <summary>
/// Specs for the <c>transactional-event-append</c> requirement on the
/// WorkflowRun producer. Covers issue-361 T-003 scenarios: state and
/// event rows commit atomically; event-row write failures roll back the
/// state transaction and are not swallowed; event rows survive a
/// crash-after-commit and remain readable on a fresh <c>DbContext</c>;
/// events for runs bound to an issue carry both <c>projectid</c> and
/// <c>issueid</c> in <c>extensions</c> (D5 identity stamping).
/// </summary>
public class TransactionalEventAppendTests : IAsyncLifetime
{
    private const string ProjectId = "proj_txn";
    private const string IssueId = "issue_txn_1";

    private readonly SqliteConnection _keeper;
    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    private EventStore _eventStore = null!;

    public TransactionalEventAppendTests()
    {
        var connectionString = $"Data Source=transactional-event-append-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _dbFactory = new Factory(_options);

        using (var db = new MohistDbContext(_options))
        {
            db.Database.EnsureCreated();
        }
        _eventStore = new EventStore(_dbFactory, NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAsync_CommitsStateAndEventRowsTogether()
    {
        var store = new WorkflowRunStore(_dbFactory, _eventStore);
        var run = BuildRun("wr_txn_ok", includeAnnotations: true);

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunCompleted(),
        ]);

        // Both rows visible on a fresh DbContext (i.e. they were
        // committed atomically with the state row, not staged on a
        // throwaway context).
        var stored = await _eventStore.ListAsync("wr_txn_ok");
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.started");
        Assert.Contains(stored, s => s.Envelope.Type == "com.mohist.workflow.run.completed");

        var loaded = await store.LoadAsync("wr_txn_ok");
        Assert.NotNull(loaded);
        Assert.Equal("wr_txn_ok", loaded!.Id);
    }

    [Fact]
    public async Task SaveAsync_EventRowWriteFailure_RollsBackStateAndEvents_AndDoesNotSwallow()
    {
        // ThrowingEventStore rejects the second AppendAsync call so the
        // save transaction fails mid-way. The exception must propagate
        // out of SaveAsync, and neither the state row nor any event row
        // may be persisted — there is no bare catch to swallow the
        // failure.
        var store = new WorkflowRunStore(_dbFactory, new ThrowingEventStore());
        var run = BuildRun("wr_txn_fail", includeAnnotations: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAsync(run, [
                new WorkflowRunStarted(),
                new WorkflowRunCompleted(),
            ]));
        Assert.Contains("event write failed", ex.Message);

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.WorkflowRuns.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.WorkflowRunEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SaveAsync_CrashAfterCommit_EventRowsRemainDurableOnFreshDbContext()
    {
        // Crash-after-commit simulation: open the save transaction,
        // commit it, then re-open the database on a brand new DbContext
        // and assert the event rows are still there. No post-commit
        // publish loop remains in the store; the only durable artefact
        // is the committed row.
        var store = new WorkflowRunStore(_dbFactory, _eventStore);
        var run = BuildRun("wr_txn_crash", includeAnnotations: true);

        await store.SaveAsync(run, [new WorkflowRunCompleted()]);

        await using var freshDb = new MohistDbContext(_options);
        var rows = await freshDb.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/workflow-runs/wr_txn_crash")
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("com.mohist.workflow.run.completed", row.Type);
        Assert.Null(row.DispatchedAt);

        var runRow = Assert.Single(await freshDb.WorkflowRuns.AsNoTracking()
            .Where(r => r.WorkflowRunId == "wr_txn_crash")
            .ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(runRow.State));
    }

    [Fact]
    public async Task SaveAsync_RunBoundToIssue_StampsProjectIdAndIssueIdOnExtensions()
    {
        // Identity stamping at write time: the run's
        // Annotations["projectId"] and Annotations["issueId"] flow onto
        // every emitted WorkflowRun event's extensions. A consumer can
        // read issueid directly from extensions without doing a reverse
        // database lookup.
        var store = new WorkflowRunStore(_dbFactory, _eventStore);
        var run = BuildRun("wr_txn_identity", includeAnnotations: true);

        await store.SaveAsync(run, [
            new WorkflowRunStarted(),
            new WorkflowRunCompleted(),
        ]);

        var stored = await _eventStore.ListAsync("wr_txn_identity");
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.True(entry.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
            Assert.Equal(ProjectId, stampedProjectId);
            Assert.True(entry.Envelope.Extensions.TryGetValue("issueid", out var stampedIssueId));
            Assert.Equal(IssueId, stampedIssueId);
        }
    }

    [Fact]
    public async Task SaveAsync_RunWithoutIssueAnnotation_DoesNotStampIssueIdExtension()
    {
        // A WorkflowRun that is not bound to an issue (e.g. a workflow
        // started by an ad-hoc API call without an issue context) must
        // NOT stamp a phantom issueid — the
        // extension is conditional on the annotation being present.
        var store = new WorkflowRunStore(_dbFactory, _eventStore);
        var run = new WorkflowRun
        {
            Id = "wr_txn_unbound",
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = ProjectId,
                }),
            Stages = [],
        };

        await store.SaveAsync(run, [new WorkflowRunCompleted()]);

        var stored = Assert.Single(await _eventStore.ListAsync("wr_txn_unbound"));
        Assert.True(stored.Envelope.Extensions.TryGetValue("projectid", out var stampedProjectId));
        Assert.Equal(ProjectId, stampedProjectId);
        Assert.False(stored.Envelope.Extensions.ContainsKey("issueid"));
    }

    [Fact]
    public async Task SaveAsync_NoEvents_StillCommitsStateRow()
    {
        // The SaveAsync(run, events) overload is the transactional
        // entry point; when no events are supplied it should still
        // commit the state row cleanly with no spurious event rows.
        var store = new WorkflowRunStore(_dbFactory, _eventStore);
        var run = BuildRun("wr_txn_state_only", includeAnnotations: true);

        await store.SaveAsync(run, []);

        var loaded = await store.LoadAsync("wr_txn_state_only");
        Assert.NotNull(loaded);
        Assert.Empty(await _eventStore.ListAsync("wr_txn_state_only"));
    }

    private static WorkflowRun BuildRun(string id, bool includeAnnotations)
    {
        Dictionary<string, string>? annotations = null;
        if (includeAnnotations)
        {
            annotations = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["projectId"] = ProjectId,
                ["issueId"] = IssueId,
                ["issueNumber"] = "1",
            };
        }
        return new WorkflowRun
        {
            Id = id,
            Metadata = new WorkflowRunMetadata(
                Name: null,
                CreatedAt: DateTimeOffset.UnixEpoch,
                Annotations: annotations),
            Stages = [],
        };
    }

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// <see cref="IEventStore"/> that throws on the second append in a
    /// save transaction, simulating an event-row write failure (e.g.
    /// constraint violation). Used to verify that the store does NOT
    /// swallow the exception and that the state transaction is rolled
    /// back.
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
            await db.WorkflowRunEvents.AddAsync(new WorkflowRunEventRow
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

        public Task MarkDispatchedAsync(string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
    }
}
