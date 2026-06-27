using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Events.Hosting;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Tests.Support;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Events;

public class EpicReconciliationServiceSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public void DefaultReconciliationPeriod_RecoversRunningEpicsPromptly()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), EpicReconciliationOptions.DefaultReconciliationPeriod);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_ReadyIdleEpicMissedEvent_TransitionsToDone()
    {
        // Simulates a missed com.mohist.issue.work-completed event: all
        // linked issues are done, but the epic is still idle. The
        // sweep is the safety net that catches this and transitions
        // the epic to done.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", grains.Calls[0].GrainKey);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_PausedEpic_IsSkippedByCandidateQuery()
    {
        // Paused epics must not be auto-done by the sweep. Because the
        // candidate query filters to Status IN ('idle','running'), a
        // paused epic is excluded at the SQL layer; the grain is never
        // invoked.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_paused", status: "paused", pauseReason: "on hold");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_paused", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_paused");
        Assert.Equal("paused", stored.Status);
        Assert.Equal("on hold", stored.PauseReason);
        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_DoneAndClosedEpics_AreSkippedByCandidateQuery()
    {
        // Terminal epics (done/closed) must not be re-invoked by the
        // sweep. Same SQL-layer exclusion as paused.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_done", status: "done");
        await SeedEpicAsync(database, epicId: "epic_closed", status: "closed");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_done", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var doneStored = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_done");
        var closedStored = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_closed");
        Assert.Equal("done", doneStored.Status);
        Assert.Equal("closed", closedStored.Status);
        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_IdleEpicWithOpenIssue_StaysIdleAndGrainNoOps()
    {
        // Sweep reaches the idle epic (it IS a candidate) but the
        // grain's ReconcileAfterTerminalAsync short-circuits on
        // open > 0 for an idle epic (no TryStartNext on idle);
        // the epic stays idle.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
        Assert.Single(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_RepeatedRuns_AreIdempotent()
    {
        // Re-running the sweep must not error and must not toggle the
        // epic's status once it has reached the terminal state.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        // The candidate set is filtered to Status IN ('idle','running'),
        // so once the epic is done the second/third runs no longer
        // invoke the grain at all.
        Assert.Single(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_NoActiveEpics_DoesNotInvokeGrain()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_done", status: "done");
        await SeedEpicAsync(database, epicId: "epic_paused", status: "paused");

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Empty(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_IdleEpicWithCancelledIssueOnly_TransitionsToDone()
    {
        // Cancelled is terminal for readiness, so an epic whose only
        // linked issue is cancelled has no open linked issues. The
        // sweep reaches the grain via the same ReconcileAfterTerminal
        // path that drives auto-done on terminal events; the grain
        // must now mark the epic done.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        Assert.Single(grains.Calls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_MultipleIdleEpics_FansOutAcrossAllCandidates()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_ready_a", status: "idle");
        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_ready_b", status: "idle");
        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_unready", status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_a1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_b1", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_unready1", issueNumber: 3, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        await SeedLinkAsync(database, epicId: "epic_ready_a", issueId: "issue_a1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_ready_b", issueId: "issue_b1", issueNumber: 2);
        await SeedLinkAsync(database, epicId: "epic_unready", issueId: "issue_unready1", issueNumber: 3);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Equal(3, grains.Calls.Count);
        await using var verify = database.CreateDbContext();
        var statuses = await verify.Epics.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Status);
        Assert.Equal("done", statuses["epic_ready_a"]);
        Assert.Equal("done", statuses["epic_ready_b"]);
        Assert.Equal("idle", statuses["epic_unready"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_ReadyEpicAfterFirstBatch_IsReached()
    {
        await using var database = CreateDatabase();
        for (var i = 0; i < 500; i++)
        {
            var epicId = $"epic_unready_{i:D3}";
            var issueId = $"issue_unready_{i:D3}";
            await SeedEpicAsync(database, projectId: "project_1", epicId: epicId, status: "idle");
            await SeedIssueAsync(database, projectId: "project_1", issueId: issueId, issueNumber: i + 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
            await SeedLinkAsync(database, epicId: epicId, issueId: issueId, issueNumber: i + 1);
        }
        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_z_ready", status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_z_ready", issueNumber: 1001, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_z_ready", issueId: "issue_z_ready", issueNumber: 1001);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        await using var verify = database.CreateDbContext();
        var ready = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_z_ready");
        Assert.Equal("done", ready.Status);
        Assert.Equal(501, grains.Calls.Count);
        Assert.Contains(grains.Calls, call => call.GrainKey == "project_1:epic_z_ready");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_RunningEpic_IsCandidateAndInvokesReconcile()
    {
        // A running epic is a candidate: a missed work-completed or
        // closed event would otherwise leave it stuck waiting for
        // the in-progress slot to clear. The sweep covers running
        // epics exactly like idle epics — the grain's
        // ReconcileAfterTerminalAsync short-circuits no-ops for the
        // running-but-stable case.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        // Single grain call to the running epic — the in-progress
        // slot is occupied, so TryStartNext returns without
        // starting another issue (serial slot held). The grain is
        // still invoked so that, if a previous terminal event was
        // missed, the sweep can recover it.
        var call = Assert.Single(grains.Calls);
        Assert.Equal("project_1:epic_1", call.GrainKey);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("running", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_RunningEpicMissedClosedEvent_AdvancesNext()
    {
        // The risk that motivates the running-epic sweep: a missed
        // com.mohist.issue.closed event would deadlock the epic
        // because the in-progress slot stays occupied. After the
        // issue is observed as cancelled in the DB and the sweep
        // runs ReconcileAfterTerminalAsync, TryStartNext must pick
        // the next startable issue.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        // The EpicGrain's ReconcileAfterTerminalAsync advances the
        // next startable issue (issue_2); the epic remains running.
        Assert.Equal("running", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_RunningEpicMissedDoneEventWithAllComplete_AutoMarksDone()
    {
        // A running epic whose last in-progress issue is observed as
        // done in the DB (terminal event lost) must auto-mark done
        // via the sweep — the same path the live work-completed
        // handler drives.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_2", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_2", issueNumber: 2);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Single(grains.Calls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_MixedIdleAndRunningEpics_AllReachTheGrain()
    {
        // Both idle (auto-done readiness) and running (terminal-event
        // recovery) candidates are walked in the same sweep — they
        // share ReconcileAfterTerminalAsync.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_idle_ready", status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_idle_ready", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedLinkAsync(database, epicId: "epic_idle_ready", issueId: "issue_idle_ready", issueNumber: 1);

        await SeedEpicAsync(database, projectId: "project_1", epicId: "epic_running_stuck", status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_stuck_cancelled", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_stuck_next", issueNumber: 3, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        await SeedLinkAsync(database, epicId: "epic_running_stuck", issueId: "issue_stuck_cancelled", issueNumber: 2);
        await SeedLinkAsync(database, epicId: "epic_running_stuck", issueId: "issue_stuck_next", issueNumber: 3);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();

        Assert.Equal(2, grains.Calls.Count);
        Assert.Contains(grains.Calls, c => c.GrainKey == "project_1:epic_idle_ready");
        Assert.Contains(grains.Calls, c => c.GrainKey == "project_1:epic_running_stuck");

        await using var verify = database.CreateDbContext();
        var idleReady = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_idle_ready");
        Assert.Equal("done", idleReady.Status);
        var runningStuck = await verify.Epics.AsNoTracking().FirstAsync(e => e.Id == "epic_running_stuck");
        // Running epic advances the next startable, stays running.
        Assert.Equal("running", runningStuck.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ReconcileOnceAsync_RunningEpicRepeatedSweeps_AreIdempotent()
    {
        // Repeated sweeps on a running epic are idempotent: the
        // grain call happens each time, but the epic state does not
        // toggle once the missed event has been recovered.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueId: "issue_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        await SeedLinkAsync(database, epicId: "epic_1", issueId: "issue_1", issueNumber: 1);

        var grains = new TestEpicGrainFactory(database.Factory);
        var service = new EpicReconciliationService(
            database.Factory, grains, NullLogger<EpicReconciliationService>.Instance);

        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();
        await service.ReconcileOnceAsync();

        // Each sweep is a fresh candidate walk — the epic remains a
        // running candidate until it transitions, so all three
        // sweeps reach the grain. The grain is idempotent; no state
        // toggles after the first call.
        Assert.Equal(3, grains.Calls.Count);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("running", stored.Status);
    }

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = "project_1",
        string epicId = "epic_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
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
            PauseReason = pauseReason,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedIssueAsync(
        TestDatabase database,
        string projectId,
        string issueId,
        int issueNumber,
        Mohist.Server.Issue.Domain.IssueStatus status)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            IssueId = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            State = json,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLinkAsync(TestDatabase database, string epicId, string issueId, int issueNumber)
    {
        await using var db = database.CreateDbContext();
        db.EpicIssues.Add(new EpicIssueRow
        {
            EpicId = epicId,
            ProjectId = "project_1",
            IssueId = issueId,
            IssueNumber = issueNumber,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        using (var db = factory.CreateDbContext())
            db.Database.Migrate();
        return new TestDatabase(connection, factory);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDatabase(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options)
        {
            Options = options;
        }

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    private sealed class TestEpicGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<RecordedGrainCall> Calls { get; } = [];

        public TestEpicGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IEpicGrain GetEpicGrain(string grainKey)
        {
            Calls.Add(new RecordedGrainCall(grainKey));
            return new EpicGrain(_dbFactory, this, NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };
        }

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            throw new NotSupportedException(typeof(TGrainInterface).FullName);
        }

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IEpicGrain))
                return GetEpicGrain(grainPrimaryKey);
            throw new NotSupportedException(grainInterfaceType.FullName);
        }
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

    public sealed record RecordedGrainCall(string GrainKey);
}
