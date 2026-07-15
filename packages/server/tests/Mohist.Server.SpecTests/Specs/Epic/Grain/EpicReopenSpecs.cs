using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Domain.Events;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using System.Data.Common;
using System.Threading;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

/// <summary>
/// Specs for issue-94 T-002: <see cref="EpicGrain.ReopenAsync"/>.
/// Exercises the domain transition (<c>Epic.Reopen</c>), the
/// active-membership re-claim that honors the cross-epic
/// uniqueness invariant (skipping re-homed issues silently), and
/// the persisted <see cref="EpicReopened"/> + <see cref="EpicStatusChanged"/>
/// events.
/// </summary>
public class EpicReopenSpecs
{
    private const string ProjectId = "project_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnClosedEpic_TransitionsToIdle()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "closed");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        var dto = await grain.ReopenAsync();

        Assert.Equal("idle", dto.Status);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
        Assert.Equal("idle", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnDoneEpic_TransitionsToIdle()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        var dto = await grain.ReopenAsync();

        Assert.Equal("idle", dto.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnIdleEpic_ThrowsNotTerminalAndStateUnchanged()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        var ex = await Assert.ThrowsAsync<EpicNotTerminalException>(() => grain.ReopenAsync());

        Assert.Equal("idle", ex.CurrentStatus);
        Assert.Equal("epic_1", ex.EpicId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
        Assert.Equal("idle", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnRunningEpic_ThrowsNotTerminalAndStateUnchanged()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        var ex = await Assert.ThrowsAsync<EpicNotTerminalException>(() => grain.ReopenAsync());

        Assert.Equal("running", ex.CurrentStatus);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
        Assert.Equal("running", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnPausedEpic_ThrowsNotTerminalAndStateUnchanged()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        var ex = await Assert.ThrowsAsync<EpicNotTerminalException>(() => grain.ReopenAsync());

        Assert.Equal("paused", ex.CurrentStatus);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
        Assert.Equal("paused", row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_ReestablishesActiveMembershipsForLinkedIssues()
    {
        // Linked issues whose EpicActiveIssueRow was released on
        // terminalization are re-claimed on reopen.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "closed");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_2", issueNumber: 2);
        await SeedLinkAsync(database, "issue_1", 1);
        await SeedLinkAsync(database, "issue_2", 2);

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");
        var dto = await grain.ReopenAsync();

        Assert.Equal("idle", dto.Status);

        await using var verify = database.CreateDbContext();
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == "epic_1")
            .ToListAsync();
        Assert.Equal(2, active.Count);
        Assert.Contains(active, a => a.IssueId == "issue_1");
        Assert.Contains(active, a => a.IssueId == "issue_2");

        // Link records are still in place — Reopen does not unlink.
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == "epic_1")
            .ToListAsync();
        Assert.Equal(2, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_WhenActiveMembershipInsertFails_RollsBackStatusAndCanRetry()
    {
        var interceptor = new FailEpicActiveIssueInsertInterceptor();
        var database = CreateDatabase(interceptor);
        await SeedEpicAsync(database, status: "closed");
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, "issue_1", 1);
        interceptor.Enabled = true;
        var failingGrain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        await Assert.ThrowsAsync<DbUpdateException>(() => failingGrain.ReopenAsync());

        await using (var verify = database.CreateDbContext())
        {
            var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == "epic_1");
            Assert.Equal("closed", row.Status);
            Assert.Empty(await verify.EpicActiveIssues.AsNoTracking().ToListAsync());
        }

        var retryOptions = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(database.Connection)
            .Options;
        var retryGrain = CreateGrain(new TestDbContextFactory(retryOptions), $"{ProjectId}:epic_1");

        var dto = await retryGrain.ReopenAsync();

        Assert.Equal("idle", dto.Status);
        await using var retryVerify = new MohistDbContext(retryOptions);
        var active = await retryVerify.EpicActiveIssues.AsNoTracking().SingleAsync();
        Assert.Equal("epic_1", active.EpicId);
        Assert.Equal("issue_1", active.IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_SkipsIssueRehomedToAnotherNonTerminalEpic()
    {
        // Spec scenario: an issue re-homed to another non-terminal
        // epic during the terminal period is silently skipped on
        // reopen. The link record stays, the issue is not
        // re-claimed, and the remaining linked issues still re-claim.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_target", status: "closed", number: 1);
        await SeedEpicAsync(database, epicId: "epic_other", status: "idle", number: 2);
        await SeedIssueAsync(database, issueId: "issue_rehomed", issueNumber: 1);
        await SeedIssueAsync(database, issueId: "issue_keep", issueNumber: 2);
        await SeedLinkAsync(database, "issue_rehomed", 1, epicId: "epic_target");
        await SeedLinkAsync(database, "issue_keep", 2, epicId: "epic_target");

        // Simulate the rehoming: another non-terminal epic claims
        // the issue during the terminal period.
        await using (var seed = database.CreateDbContext())
        {
            seed.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = ProjectId,
                IssueId = "issue_rehomed",
                EpicId = "epic_other",
                IssueNumber = 1,
            });
            await seed.SaveChangesAsync();
        }

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_target");
        var dto = await grain.ReopenAsync();

        Assert.Equal("idle", dto.Status);

        await using var verify = database.CreateDbContext();
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId)
            .ToListAsync();

        // issue_rehomed is still owned by epic_other; not re-claimed
        // by epic_target.
        var rehomedRow = Assert.Single(active, a => a.IssueId == "issue_rehomed");
        Assert.Equal("epic_other", rehomedRow.EpicId);

        // issue_keep was re-claimed by epic_target.
        var keepRow = Assert.Single(active, a => a.IssueId == "issue_keep");
        Assert.Equal("epic_target", keepRow.EpicId);

        // Link records survive in epic_target.
        var links = await verify.EpicIssues.AsNoTracking()
            .Where(l => l.EpicId == "epic_target")
            .ToListAsync();
        Assert.Equal(2, links.Count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_RestagesIssueAndWorkflowToItsReclaimedActiveMembership()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_reopened", status: "closed", number: 1);
        await SeedEpicAsync(database, epicId: "epic_retained", status: "done", number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, workflowRunId: "workflow_1", epicId: "epic_retained");
        await SeedWorkflowAsync(database, "workflow_1", "epic_retained");
        await SeedLinkAsync(database, "issue_1", 1, epicId: "epic_reopened");
        await SeedLinkAsync(database, "issue_1", 1, epicId: "epic_retained");

        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_reopened");
        await grain.ReopenAsync();

        await using var verify = database.CreateDbContext();
        Assert.Equal("epic_reopened", (await verify.Issues.SingleAsync(row => row.IssueId == "issue_1")).EpicId);
        Assert.Equal("epic_reopened", (await verify.WorkflowRuns.SingleAsync(row => row.WorkflowRunId == "workflow_1")).EpicId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_RecordsEpicReopenedAndEpicStatusChangedEvents()
    {
        var database = CreateDatabase();
        var eventStore = new RecordingEventStore();
        await SeedEpicAsync(database, status: "closed");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1", eventStore);

        await grain.ReopenAsync();

        var events = await eventStore.ListEpicEventsAsync("epic_1");
        var reopened = Assert.Single(events, e => e.Envelope.Type == EventCatalog.ReverseDns.EpicReopened);
        Assert.Equal(EventCatalog.ReverseDns.EpicReopened, reopened.Envelope.Type);

        var statusChanges = events.Where(e => e.Envelope.Type == EventCatalog.ReverseDns.EpicStatusChanged).ToList();
        var reopenTransition = statusChanges.Last();
        Assert.Equal("closed", reopenTransition.Envelope.Data?.GetProperty("oldStatus").GetString());
        Assert.Equal("idle", reopenTransition.Envelope.Data?.GetProperty("newStatus").GetString());

        // The dedicated EpicReopened event must follow the
        // EpicStatusChanged event in the chronological stream.
        Assert.True(reopened.Id > reopenTransition.Id,
            "Expected EpicReopened to be persisted after the EpicStatusChanged(closed->idle) event");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_OnMissingEpic_ThrowsInvalidOperation()
    {
        var database = CreateDatabase();
        var grain = CreateGrain(database.Factory, $"{ProjectId}:nonexistent");

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.ReopenAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReopenAsync_EnsureNotTerminalStillBlocksOtherTransitionsAfter()
    {
        // Regression: Start/Pause/Resume/Done/Close from terminal
        // remain blocked; Reopen is the only exit. After reopening,
        // Start succeeds.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "closed");
        var grain = CreateGrain(database.Factory, $"{ProjectId}:epic_1");

        await Assert.ThrowsAsync<EpicAlreadyTerminalException>(() => grain.StartAsync());
        await Assert.ThrowsAsync<EpicAlreadyTerminalException>(() => grain.PauseAsync(null));
        await Assert.ThrowsAsync<EpicAlreadyTerminalException>(() => grain.ResumeAsync());

        await grain.ReopenAsync();

        // After reopen, Start works again.
        var dto = await grain.StartAsync();
        Assert.Equal("running", dto.Status);
    }

    private static EpicGrain CreateGrain(TestDbContextFactory factory, string grainKey, IEventStore? eventStore = null) =>
        new(
            factory,
            new NullGrainFactory(),
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            eventStore ?? new NoopEventStore(),
            NullLogger<EpicGrain>.Instance)
        {
            GrainKeyForTest = grainKey,
        };

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string epicId = "epic_1",
        int number = 1,
        string status = "idle")
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            Id = epicId,
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {epicId}",
            Description = "",
            Priority = "p2",
            Status = status,
            CreatedAt = TestTime.UtcNow,
            UpdatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string issueId = "issue_1",
        int issueNumber = 1,
        string? workflowRunId = null,
        string? epicId = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = IssueStatus.Backlog,
            Priority = "p2",
            IsDraft = false,
            WorkflowRunId = workflowRunId,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
            EpicId = epicId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedWorkflowAsync(TestDatabase database, string workflowRunId, string? epicId)
    {
        await using var db = database.CreateDbContext();
        db.WorkflowRuns.Add(new WorkflowRunRow
        {
            WorkflowRunId = workflowRunId,
            State = "{}",
            EpicId = epicId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLinkAsync(
        TestDatabase database,
        string issueId,
        int issueNumber,
        string projectId = ProjectId,
        string epicId = "epic_1")
    {
        await using var db = database.CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = projectId,
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = TestTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static TestDatabase CreateDatabase(DbCommandInterceptor? interceptor = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var builder = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        var options = builder.Options;
        var factory = new TestDbContextFactory(options);
        MigratedSqliteTemplate.CopyTo(connection);
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Connection = connection;
            Factory = factory;
        }

        public SqliteConnection Connection { get; }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class FailEpicActiveIssueInsertInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfEpicActiveIssueInsert(command, Enabled);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfEpicActiveIssueInsert(command, Enabled);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfEpicActiveIssueInsert(command, Enabled);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfEpicActiveIssueInsert(command, Enabled);
            return ValueTask.FromResult(result);
        }

        private static void ThrowIfEpicActiveIssueInsert(DbCommand command, bool enabled)
        {
            if (enabled
                && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("EpicActiveIssues", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected active-membership insert failure");
        }
    }

    /// <summary>
    /// No-op Orleans grain factory: the reopen paths under test
    /// never touch issue grains.
    /// </summary>
    private sealed class NullGrainFactory : IGrainFactory
    {
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithStringKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null) where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension) => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey, string? grainClassNamePrefix = null) => throw new NotSupportedException();
        public IAddressable GetGrain(Type interfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }
}
