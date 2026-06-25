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
    public async Task ReconcileOnceAsync_IdleEpicWithIncompleteIssue_StaysIdleAndGrainNoOps()
    {
        // Sweep reaches the idle epic (it IS a candidate) but the
        // grain's AutoMarkDoneIfReadyAsync short-circuits on
        // undelivered > 0; the epic stays idle.
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
    public async Task ReconcileOnceAsync_IdleEpicWithCancelledIssueOnly_StaysIdle()
    {
        // Cancelled issues are not treated as complete by the readiness
        // check; the sweep should reach the epic (it's idle) and the
        // grain should no-op because undelivered is non-empty.
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
        Assert.Equal("idle", stored.Status);
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
            return new EpicGrain(_dbFactory, this) { GrainKeyForTest = grainKey };
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
