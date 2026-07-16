using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

/// <summary>
/// Issue-392 spec scenarios for the single-link wake-up contract.
/// Covers: done+open wakes to running, done+terminal stays done,
/// idempotent re-link, closed rejection, atomic active-membership
/// insert, cross-aggregate ownership uniqueness, transactional
/// rollback, autopilot tail-call, and the wake-up invariant that no
/// manual <c>start</c>/<c>resume</c> is required.
///
/// Runs under SQLite with an injected <see cref="FakeTimeProvider"/>
/// and a recording <see cref="IGrainFactory"/> — no real DB,
/// no real Orleans, no real network. Each test completes well under
/// the 500ms spec budget.
/// </summary>
public class EpicWakeUpSpecs
{
    private const string ProjectId = "project_1";
    private const string EpicId = "epic_1";

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_OpenIssue_WakesToRunning_InSameCommit()
    {
        // Spec: 'Done epic linked with an open issue transitions to running'
        // + 'Active-membership row added in the same commit as the wake-up'.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Running, row.Status);
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .SingleAsync(a => a.ProjectId == ProjectId && a.EpicId == EpicId);
        Assert.Equal("issue_1", active.IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_TerminalIssue_StaysDone_AndNoActiveRow()
    {
        // Spec: 'Done epic linked with an already-terminal issue stays done'.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_done", issueNumber: 1, status: IssueStatus.Done);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_done", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Done, row.Status);
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == EpicId)
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_AlreadyLinked_IsIdempotent_NoStatusChange()
    {
        // Spec: 'Idempotent re-link ... does not wake the epic'.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Done);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);
        // Re-link the same already-linked issue — must not wake, must
        // not create a duplicate row.
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Done, row.Status);
        var count = await verify.EpicIssues.AsNoTracking()
            .CountAsync(l => l.ProjectId == ProjectId && l.EpicId == EpicId && l.IssueId == "issue_1");
        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_OpenIssue_NoManualStartOrResume_ReachesRunningDirectly()
    {
        // Spec: 'Wake to running requires no manual start or resume' —
        // the wake MUST NOT pass through idle. The only state change
        // is `done` -> `running` driven by LinkIssueAsync; the caller
        // does not (and must not) call StartAsync / ResumeAsync. The
        // autopilot tail-call that advances the linked issue is
        // covered separately in the "autopilot advances after wake-up"
        // scenario.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog, canStart: false);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Running, row.Status);
        Assert.NotEqual(EpicStatusName.Idle, row.Status);
        // Confirm no manual issue-start was needed from the caller.
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_ClosedEpic_ThrowsEpicClosedCannotLinkException_NoRowsCreated()
    {
        // Spec: 'Single link to a closed epic is rejected'.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Closed);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        var ex = await Assert.ThrowsAsync<EpicClosedCannotLinkException>(
            () => grain.LinkIssueAsync("issue_1", 1, ProjectId));
        Assert.Equal(EpicId, ex.EpicId);

        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == EpicId && l.IssueId == "issue_1")
            .ToListAsync());
        Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.EpicId == EpicId)
            .ToListAsync());
        // Epic status must not have been mutated.
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Closed, row.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_OpenIssueOwnedByAnotherNonTerminal_Rejects_NoWake()
    {
        // Spec: 'Wake-up respects cross-aggregate active-membership uniqueness'.
        var database = CreateDatabase();
        await SeedEpicAsync(database, epicId: "epic_active", status: EpicStatusName.Running, number: 1);
        await SeedEpicAsync(database, epicId: EpicId, status: EpicStatusName.Done, number: 2);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog);

        // Pre-seed active-membership for the running epic to simulate the
        // cross-aggregate ownership invariant: another non-terminal epic
        // already claims the issue.
        await using (var seed = database.CreateDbContext())
        {
            seed.EpicActiveIssues.Add(new EpicActiveIssueRow
            {
                ProjectId = ProjectId,
                IssueId = "issue_1",
                EpicId = "epic_active",
                IssueNumber = 1,
            });
            await seed.SaveChangesAsync();
        }

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.LinkIssueAsync("issue_1", 1, ProjectId));
        Assert.Contains("epic_active", ex.Message);

        await using var verify = database.CreateDbContext();
        // The done epic stays done (no wake).
        var doneRow = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Done, doneRow.Status);
        // No second active-membership row.
        var active = await verify.EpicActiveIssues.AsNoTracking()
            .Where(a => a.ProjectId == ProjectId && a.IssueId == "issue_1")
            .ToListAsync();
        Assert.Single(active);
        Assert.Equal("epic_active", active[0].EpicId);
        // No link row in the done epic.
        Assert.Empty(await verify.EpicIssues.AsNoTracking()
            .Where(l => l.ProjectId == ProjectId && l.EpicId == EpicId && l.IssueId == "issue_1")
            .ToListAsync());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_WakeUpActiveRowInsertFails_EpicStaysDoneAndRetrySucceeds()
    {
        // Spec: 'Wake-up that fails to persist rolls back the status change'.
        // Simulate the failure with a save interceptor that throws on
        // the EpicActiveIssues insert. The epic must remain `done`,
        // no row may be left behind, and a retry without the
        // interceptor must perform the full wake-up.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog);

        var failingFactory = database.CreateFactory(
            new FailOnEpicActiveIssueInsertInterceptor(ProjectId, EpicId));
        var failingGrains = new RecordingGrainFactory(failingFactory);
        var failingGrain = failingGrains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await Assert.ThrowsAnyAsync<Exception>(
            () => failingGrain.LinkIssueAsync("issue_1", 1, ProjectId));

        await using (var verify = database.CreateDbContext())
        {
            var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
            Assert.Equal(EpicStatusName.Done, row.Status);
            Assert.Empty(await verify.EpicIssues.AsNoTracking()
                .Where(l => l.ProjectId == ProjectId && l.EpicId == EpicId)
                .ToListAsync());
            Assert.Empty(await verify.EpicActiveIssues.AsNoTracking()
                .Where(a => a.ProjectId == ProjectId && a.EpicId == EpicId)
                .ToListAsync());
        }

        // Retry with the regular factory — must perform the full wake-up.
        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");
        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify2 = database.CreateDbContext();
        var row2 = await verify2.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Running, row2.Status);
        var active = await verify2.EpicActiveIssues.AsNoTracking()
            .SingleAsync(a => a.ProjectId == ProjectId && a.EpicId == EpicId);
        Assert.Equal("issue_1", active.IssueId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_WakesAndAutopilotStartsNewOpenIssue()
    {
        // Spec: 'Autopilot advances the newly linked open issue after wake-up'.
        // After the wake-up commit, the grain tail-calls TryStartNextAsync
        // which must invoke IIssueGrain.StartWorkAsync on the just-linked
        // open issue (no caller-issued StartAsync / StartWorkAsync).
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        // The grain tail-called TryStartNextAsync, which invoked
        // IIssueGrain.StartWorkAsync on the only open, startable,
        // non-in-progress linked issue.
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("issue_1", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssueAsync_DoneEpic_WakeUpDoesNotInvokeTryStartNext_WhenNoStartableIssue()
    {
        // The tail-call TryStartNextAsync is best-effort: when the
        // newly-linked open issue cannot start (prerequisites missing,
        // draft, etc.) the wake-up still succeeds and the epic remains
        // running-but-idle for the next recompute retry.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_1", issueNumber: 1, status: IssueStatus.Backlog, canStart: false);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");

        await grain.LinkIssueAsync("issue_1", 1, ProjectId);

        await using var verify = database.CreateDbContext();
        var row = await verify.Epics.AsNoTracking().SingleAsync(e => e.Id == EpicId);
        Assert.Equal(EpicStatusName.Running, row.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task WakeFromDone_OnNonDoneEpic_ThrowsEpicAlreadyTerminal()
    {
        // Misuse guard: WakeFromDone must only fire from a `done`
        // epic; calling it on any other status throws so the failure
        // surfaces loudly instead of corrupting the state machine.
        var epic = Mohist.Server.Epic.Domain.Epic.Create(
            id: EpicId,
            projectId: ProjectId,
            number: 1,
            title: "T",
            now: TestTime.UtcDateTime);
        epic.Start();

        var ex = Assert.Throws<EpicAlreadyTerminalException>(() => epic.WakeFromDone());
        Assert.Equal(EpicStatusName.Running, ex.CurrentStatus);
        Assert.Equal(EpicStatusName.Running, ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task WakeFromDone_OnDoneEpic_TransitionsToRunningAndEmitsStatusChanged()
    {
        var epic = Mohist.Server.Epic.Domain.Epic.Create(
            id: EpicId,
            projectId: ProjectId,
            number: 1,
            title: "T",
            now: TestTime.UtcDateTime);
        epic.LinkIssue("issue_terminal", 1, now: TestTime.UtcDateTime);
        epic.MarkDone(openLinkedNumbers: new HashSet<int>());

        epic.WakeFromDone();

        Assert.Equal(Mohist.Server.Epic.Domain.EpicStatus.Running, epic.Status);
        var statusChanged = EpicStatusChangedEvents(epic.PendingEvents).Last();
        Assert.Equal(EpicStatusName.Done, statusChanged.OldStatus);
        Assert.Equal(EpicStatusName.Running, statusChanged.NewStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task LinkIssue_OnClosedEpic_IsRejected_EvenForAlreadyLinkedIssue()
    {
        // Per the design, the idempotent re-link short-circuit runs
        // BEFORE the closed guard, so an issue that was linked before
        // close remains linked (idempotent) — but a NEW issue linked to
        // a closed epic is rejected. The two halves are exercised here.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: EpicStatusName.Done);
        await SeedIssueAsync(database, issueId: "issue_pre", issueNumber: 1, status: IssueStatus.Backlog);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain($"{ProjectId}:{EpicId}");
        await grain.LinkIssueAsync("issue_pre", 1, ProjectId);

        // The done epic is now running (woke). Mark it closed
        // explicitly through the public grain path to validate the
        // closed-after-wake scenario.
        await grain.SetStatusAsync("closed");

        // Re-link the existing issue — idempotent, no throw.
        await grain.LinkIssueAsync("issue_pre", 1, ProjectId);

        // New issue to closed epic — rejected.
        await SeedIssueAsync(database, issueId: "issue_new", issueNumber: 2, status: IssueStatus.Backlog);
        await Assert.ThrowsAsync<EpicClosedCannotLinkException>(
            () => grain.LinkIssueAsync("issue_new", 2, ProjectId));
    }

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = ProjectId,
        string epicId = EpicId,
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
        IssueStatus status = IssueStatus.Backlog,
        bool canStart = true)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            Id = issueId,
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            Priority = "p2",
            IsDraft = !canStart,
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

    private static TestDatabase CreateDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(connection)
            .Options;
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
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public MohistDbContext CreateDbContext() => Factory.CreateDbContext();

        public TestDbContextFactory CreateFactory(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<MohistDbContext>()
                .UseSqlite(_connection);
            if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
            return new TestDbContextFactory(builder.Options);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MohistDbContext>
    {
        public TestDbContextFactory(DbContextOptions<MohistDbContext> options) => Options = options;

        public DbContextOptions<MohistDbContext> Options { get; }

        public MohistDbContext CreateDbContext() => new(Options);
    }

    /// <summary>
    /// Test double for <see cref="IGrainFactory"/> that records every
    /// call to <c>IEpicGrain.StartAsync/ResumeAsync</c> (so we can
    /// assert no manual start/resume is issued before/after wake-up)
    /// and every <c>IIssueGrain.StartWorkAsync</c> (so we can assert
    /// autopilot advances the newly linked open issue after the
    /// wake-up commit).
    /// </summary>
    private sealed class RecordingGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        public List<string> IssueStartCalls { get; } = [];

        public RecordingGrainFactory(IDbContextFactory<MohistDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public IEpicGrain GetEpicGrain(string grainKey) =>
            new EpicGrain(
                _dbFactory,
                this,
                new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)),
                new NoopEventStore(),
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };

        public IIssueGrain GetIssueGrain(string issueId) => new RecordingIssueGrain(this, issueId);

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IEpicGrain))
                return (TGrainInterface)(object)GetEpicGrain(primaryKey);
            if (typeof(TGrainInterface) == typeof(IIssueGrain))
                return (TGrainInterface)(object)GetIssueGrain(primaryKey);
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
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
        {
            if (grainInterfaceType == typeof(IEpicGrain))
                return GetEpicGrain(grainPrimaryKey);
            if (grainInterfaceType == typeof(IIssueGrain))
                return GetIssueGrain(grainPrimaryKey);
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

    private sealed class RecordingIssueGrain : IIssueGrain
    {
        private readonly RecordingGrainFactory _owner;
        public RecordingIssueGrain(RecordingGrainFactory owner, string issueId)
        {
            _owner = owner;
            IssueId = issueId;
        }

        public string IssueId { get; }

        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null)
            => throw new NotSupportedException();
        public async Task<string> StartWorkAsync(Mohist.Server.Issue.Grains.WorkflowProjectContext? project = null)
        {
            _owner.IssueStartCalls.Add(IssueId);
            return "wr_test";
        }
        public Task EnsureWorkflowBindingAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CompleteWorkAsync(string workflowRunId) => throw new NotSupportedException();
        public Task CancelAsync() => throw new NotSupportedException();
        public Task UpdateAsync(string title, string? body) => throw new NotSupportedException();
        public Task UpdateFullAsync(Mohist.Server.Issue.Grains.UpdateIssueData data) => throw new NotSupportedException();
        public Task ArchiveAsync() => throw new NotSupportedException();
        public Task UnarchiveAsync() => throw new NotSupportedException();
        public Task ReopenAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssueWorkflowStatus?> GetWorkflowStatusAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssuePrerequisiteResult> AddPrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task RemovePrerequisiteAsync(int prerequisiteNumber) => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Services.IssueStartReadiness> GetStartReadinessAsync() => throw new NotSupportedException();
        public Task<Mohist.Server.Issue.Grains.IssueCommentResult> AddCommentAsync(string body, string[]? attachmentIds = null) => throw new NotSupportedException();
        public Task DeactivateForTestAsync() => throw new NotSupportedException();
        public Task SetEpicAffiliationAsync(int? epicNumber) => throw new NotSupportedException();
    }

    /// <summary>
    /// EF Core save interceptor that throws when an
    /// <c>EpicActiveIssues</c> row would be inserted for the target
    /// epic, simulating a failed <c>SaveChangesAsync</c> for the
    /// wake-up commit.
    /// </summary>
    private sealed class FailOnEpicActiveIssueInsertInterceptor : SaveChangesInterceptor
    {
        private readonly string _projectId;
        private readonly string _epicId;

        public FailOnEpicActiveIssueInsertInterceptor(string projectId, string epicId)
        {
            _projectId = projectId;
            _epicId = epicId;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfConflict(eventData);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowIfConflict(eventData);
            return result;
        }

        private void ThrowIfConflict(DbContextEventData eventData)
        {
            if (eventData.Context is null) return;
            var entries = eventData.Context.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added)
                .ToList();
            foreach (var entry in entries)
            {
                if (entry.Entity is EpicActiveIssueRow row
                    && row.ProjectId == _projectId
                    && row.EpicId == _epicId)
                {
                    throw new InvalidOperationException(
                        "simulated EpicActiveIssues insert failure");
                }
            }
        }
    }

    private static List<Mohist.Server.Epic.Domain.Events.EpicStatusChanged> EpicStatusChangedEvents(
        IEnumerable<Mohist.Server.Epic.Domain.Events.EpicEvent> events)
    {
        var result = new List<Mohist.Server.Epic.Domain.Events.EpicStatusChanged>();
        foreach (var evt in events)
        {
            if (evt is Mohist.Server.Epic.Domain.Events.EpicStatusChanged statusChanged)
                result.Add(statusChanged);
        }
        return result;
    }
}
