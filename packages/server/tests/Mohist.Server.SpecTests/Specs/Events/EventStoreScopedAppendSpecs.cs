using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

[Trait(Traits.Speed.Name, Traits.Speed.Unit)]
[Trait(Traits.Sut.Name, Traits.Sut.System)]
public class EventStoreScopedAppendSpecs : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);

    private readonly DbContextOptions<MohistDbContext> _options;
    private readonly SqliteConnection _keeper;
    private EventStore _store = null!;

    public EventStoreScopedAppendSpecs()
    {
        var connectionString = $"Data Source=event-store-scoped-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keeper = new SqliteConnection(connectionString);
        _keeper.Open();
        _options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        MigratedSqliteTemplate.CopyTo(_keeper);

        _store = new EventStore(new Factory(_options), NullLogger<EventStore>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _keeper.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AppendAsync_DoesNotCallSaveChangesAsync_OnScopedOverload()
    {
        await using var db = new MohistDbContext(_options);

        await _store.AppendAsync(db, BuildEvent("/mohist/workflow-runs/wr_scoped_save", "com.mohist.workflow.task.completed"));

        var pending = Assert.Single(db.WorkflowRunEvents.Local);
        Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.Equal("/mohist/workflow-runs/wr_scoped_save", pending.Source);
        Assert.Equal(1, pending.Id);
        Assert.Empty(await db.WorkflowRunEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AppendAsync_ScopedOverload_StagesRowVisibleInsideCallerTransaction()
    {
        await using var db = new MohistDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await _store.AppendAsync(db, BuildEvent("/mohist/workflow-runs/wr_scoped_visible", "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(db, BuildEvent("/mohist/workflow-runs/wr_scoped_visible", "com.mohist.workflow.task.completed"));

        var pendingInContext = db.WorkflowRunEvents.Local
            .Where(r => r.Source == "/mohist/workflow-runs/wr_scoped_visible")
            .OrderBy(r => r.Id)
            .ToList();
        Assert.Equal(new long[] { 1, 2 }, pendingInContext.Select(r => r.Id));

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task AppendAsync_ScopedOverload_RowIsDurableAfterCommit_OnFreshDbContext()
    {
        await using (var db = new MohistDbContext(_options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await _store.AppendAsync(db, BuildEvent("/mohist/workflow-runs/wr_scoped_durable", "com.mohist.workflow.task.completed"));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var fresh = new MohistDbContext(_options);
        var row = Assert.Single(await fresh.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == "/mohist/workflow-runs/wr_scoped_durable")
            .ToListAsync());
        Assert.Equal("com.mohist.workflow.task.completed", row.Type);
        Assert.Null(row.DispatchedAt);
    }

    [Fact]
    public async Task AppendAsync_PerSourceId_AccountsForPendingRowsInSameTransaction()
    {
        await using var db = new MohistDbContext(_options);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var source = "/mohist/workflow-runs/wr_seq_in_tx";
        await _store.AppendAsync(db, BuildEvent(source, "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(db, BuildEvent(source, "com.mohist.workflow.task.completed"));
        await _store.AppendAsync(db, BuildEvent(source, "com.mohist.workflow.task.completed"));

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        await using var verify = new MohistDbContext(_options);
        var ids = (await verify.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == source)
            .OrderBy(r => r.Id)
            .ToListAsync()).Select(r => r.Id).ToList();
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public async Task AppendAsync_PerSourceId_AcrossSeparateTransactions()
    {
        var source = "/mohist/workflow-runs/wr_seq_separate_tx";

        await using (var db = new MohistDbContext(_options))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await _store.AppendAsync(db, BuildEvent(source, "com.mohist.workflow.task.completed"));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using (var db = new MohistDbContext(_options))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await _store.AppendAsync(db, BuildEvent(source, "com.mohist.workflow.task.completed"));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using var verify = new MohistDbContext(_options);
        var ids = (await verify.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == source)
            .OrderBy(r => r.Id)
            .ToListAsync()).Select(r => r.Id).ToList();
        Assert.Equal(new long[] { 1, 2 }, ids);
    }

    [Fact]
    public async Task AppendAsync_NonScopedOverload_OpensOwnTransactionAndCommits()
    {
        var source = "/mohist/workflow-runs/wr_nonscoped";
        await _store.AppendAsync(BuildEvent(source, "com.mohist.workflow.task.completed"));

        await using var verify = new MohistDbContext(_options);
        var row = Assert.Single(await verify.WorkflowRunEvents.AsNoTracking()
            .Where(r => r.Source == source)
            .ToListAsync());
        Assert.Equal(1, row.Id);
    }

    [Fact]
    public async Task AppendAsync_UnregisteredMohistType_IsRejectedAtStoreBoundary()
    {
        var envelope = BuildEvent(
            "/mohist/workflow-runs/wr_unregistered",
            "com.mohist.workflow.unregistered");

        var ex = await Assert.ThrowsAsync<UnregisteredMohistEventTypeException>(() => _store.AppendAsync(envelope));

        Assert.Equal(envelope.Type, ex.EventType);
    }

    [Fact]
    public async Task AppendAsync_AgentSessionSource_LandsInAgentSessionEventsTable()
    {
        await using var db = new MohistDbContext(_options);
        var sessionId = "sess_scoped_1";

        await _store.AppendAsync(db, BuildEvent(AgentSessionEventPersistence.AgentSessionSource(sessionId), "test.agent-session.bound"));

        Assert.Empty(await db.WorkflowRunEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await db.IssueEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await db.EpicEvents.AsNoTracking().ToListAsync());

        var pending = db.AgentSessionEvents.Local.Single();
        Assert.Equal(1, pending.Id);
        Assert.Equal(AgentSessionEventPersistence.AgentSessionSource(sessionId), pending.Source);

        await db.SaveChangesAsync();

        await using var verify = new MohistDbContext(_options);
        var stored = Assert.Single(await verify.AgentSessionEvents.AsNoTracking()
            .Where(r => r.Source == AgentSessionEventPersistence.AgentSessionSource(sessionId))
            .ToListAsync());
        Assert.Equal("test.agent-session.bound", stored.Type);
        Assert.Equal(sessionId, stored.Subject);
    }

    [Fact]
    public async Task AppendAsync_AgentSessionSource_AppearsInListUndeliveredAsync()
    {
        var sessionId = "sess_scoped_undelivered";
        await _store.AppendAsync(BuildEvent(
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            "test.agent-session.bound"));

        var undelivered = await _store.ListUndeliveredAsync();
        var fromSession = Assert.Single(undelivered, r => r.Origin == EventOrigin.AgentSession);
        Assert.Equal(1, fromSession.Id);
        Assert.Equal(AgentSessionEventPersistence.AgentSessionSource(sessionId), fromSession.Source);
        Assert.Equal(sessionId, fromSession.Subject);
        Assert.Equal("test.agent-session.bound", fromSession.Type);
    }

    [Fact]
    public async Task ListUndeliveredAsync_UnionsAllOriginsInSourceIdOrder_AndMarksByOrigin()
    {
        var events = new[]
        {
            BuildEvent(WorkflowRunEventPersistence.WorkflowRunSource("zeta"), "test.workflow", "evt-workflow-1"),
            BuildEvent(IssueEventPersistence.IssueSource("alpha", 1), "test.issue", "evt-issue-1"),
            BuildEvent(IssueEventPersistence.IssueSource("alpha", 1), "test.issue", "evt-issue-2"),
            BuildEvent(EpicEventPersistence.EpicSource("middle", 1), "test.epic", "evt-epic-1"),
            BuildEvent(AgentSessionEventPersistence.AgentSessionSource("beta"), "test.agent", "evt-agent-1"),
        };
        foreach (var evt in events)
            await _store.AppendAsync(evt);

        var undelivered = await _store.ListUndeliveredAsync();

        Assert.Equal(5, undelivered.Count);
        Assert.Equal(4, undelivered.Select(row => row.Origin).Distinct().Count());
        Assert.Contains(undelivered, row => row.Origin == EventOrigin.WorkflowRun);
        Assert.Contains(undelivered, row => row.Origin == EventOrigin.Issue);
        Assert.Contains(undelivered, row => row.Origin == EventOrigin.Epic);
        Assert.Contains(undelivered, row => row.Origin == EventOrigin.AgentSession);
        Assert.Equal(
            undelivered.OrderBy(row => row.Source, StringComparer.Ordinal).ThenBy(row => row.Id)
                .Select(row => (row.Source, row.Id)),
            undelivered.Select(row => (row.Source, row.Id)));

        var epic = Assert.Single(undelivered, row => row.Origin == EventOrigin.Epic);
        await _store.MarkDispatchedAsync(epic.Origin, epic.Source, epic.Id, FixedTime);

        var remaining = await _store.ListUndeliveredAsync();
        Assert.Equal(4, remaining.Count);
        Assert.DoesNotContain(remaining, row => row.EventId == epic.EventId);
        Assert.Contains(remaining, row => row.Origin == EventOrigin.WorkflowRun);
        Assert.Contains(remaining, row => row.Origin == EventOrigin.Issue);
        Assert.Contains(remaining, row => row.Origin == EventOrigin.AgentSession);
    }

    [Fact]
    public async Task ListUndeliveredAsync_OmitsAgentSessionRowsOnceDispatched()
    {
        var sessionId = "sess_scoped_dispatched";
        await _store.AppendAsync(BuildEvent(
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            "test.agent-session.bound"));

        await _store.MarkDispatchedAsync(
            EventOrigin.AgentSession,
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            1,
            FixedTime);

        var undelivered = await _store.ListUndeliveredAsync();
        Assert.DoesNotContain(undelivered, r => r.Origin == EventOrigin.AgentSession);
    }

    [Fact]
    public async Task AppendAsync_FallbackSource_CanBeMarkedFromReportedOrigin()
    {
        var source = new Uri("test://custom-source");
        await _store.AppendAsync(new CloudEvent(
            id: "evt_custom_source",
            source: source,
            type: "test.custom",
            time: FixedTime,
            data: JsonDocument.Parse("{}").RootElement));

        var pending = Assert.Single(
            await _store.ListUndeliveredAsync(),
            row => row.EventId == "evt_custom_source");
        Assert.Equal(EventOrigin.WorkflowRun, pending.Origin);
        Assert.Equal(source.ToString(), pending.Source);

        await _store.MarkDispatchedAsync(
            pending.Origin,
            pending.Source,
            pending.Id,
            FixedTime);

        Assert.DoesNotContain(
            await _store.ListUndeliveredAsync(),
            row => row.EventId == "evt_custom_source");
    }

    [Fact]
    public async Task AppendAsync_NonScopedOverload_RoutesAgentSessionSourceToAgentSessionEvents()
    {
        var sessionId = "sess_nonscoped_routing";
        await _store.AppendAsync(BuildEvent(
            AgentSessionEventPersistence.AgentSessionSource(sessionId),
            "test.agent-session.bound"));

        await using var verify = new MohistDbContext(_options);
        Assert.Empty(await verify.WorkflowRunEvents.AsNoTracking().ToListAsync());
        var stored = Assert.Single(await verify.AgentSessionEvents.AsNoTracking()
            .Where(r => r.Source == AgentSessionEventPersistence.AgentSessionSource(sessionId))
            .ToListAsync());
        Assert.Equal(1, stored.Id);
    }

    private static CloudEvent BuildEvent(string source, string type, string? eventId = null) =>
        new(
            id: eventId ?? Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.Relative),
            type: type,
            time: FixedTime,
            data: JsonDocument.Parse("{}").RootElement,
            subject: source.Split('/').Last(),
            extensions: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EventCatalog.Lineage.ProjectId] = "proj_event_store",
                [EventCatalog.Lineage.WorkflowRunId] = source.Split('/').Last(),
                [EventCatalog.Lineage.Stage] = "test",
            });

    private sealed class Factory : IDbContextFactory<MohistDbContext>
    {
        private readonly DbContextOptions<MohistDbContext> _options;

        public Factory(DbContextOptions<MohistDbContext> options) => _options = options;

        public MohistDbContext CreateDbContext() => new(_options);
    }
}
