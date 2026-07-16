using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class EpicProgressionSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartAsync_IdleEpicWithStartableIssue_StartsIssueAndIsRunning()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.StartAsync();

        Assert.Equal("running", result.Status);
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:1", started);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("running", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartAsync_IdleEpicWithoutStartableIssue_BecomesRunningButIdle()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Backlog, canStart: false);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.StartAsync();

        Assert.Equal("running", result.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task StartAsync_AlreadyRunningEpic_IsIdempotentAndDoesNotReStart()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.InProgress, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.StartAsync();

        Assert.Equal("running", result.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_RunningEpicOnDoneIssue_AdvancesNextStartable()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 3, status: IssueStatus.Backlog, canStart: true, priority: "p0");

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        // Highest-priority startable (p0 issue_3) wins; issue_2 is p2.
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:3", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_RunningEpicOnCancelledInProgressIssue_AdvancesNext()
    {
        // The in-progress issue is cancelled (terminal), the serial
        // in-progress slot is cleared, and recompute progress must pick
        // the next startable issue. This is the scenario that requires
        // subscribing to IssueCancelled, but the recompute logic
        // itself is T-002.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Cancelled, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:2", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_RunningEpicOnDoneIssueWithAllComplete_AutoMarksDone()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Done, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("done", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_PausedEpic_IsNoOpAndDoesNotAdvance()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused", pauseReason: "on hold");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("paused", result!.Status);
        Assert.Equal("on hold", result.PauseReason);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_IdleEpic_DoesNotAdvance()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("idle", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_TerminalEpic_IsNoOp()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("done", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_FailedInProgressIssue_HoldsEpic()
    {
        // The in-progress issue is stuck (NOT a terminal state) so
        // recompute progress must not start a second issue: the serial
        // in-progress slot is occupied. The epic remains running.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.InProgress, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_NoStartableIssue_RemainsRunningButIdle()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: false);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_CancelledIssueIsSkipped_NextStartableChosen()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Cancelled, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:2", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_CancelledLinkedPrerequisite_DoesNotStartDependent()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Cancelled, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true, prerequisiteNumbers: [1]);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.Equal("running", result!.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_TryStartFromEpicAsyncThrows_PropagatesToDispatcher()
    {
        // Terminal-event recompute uses StartFailureMode.Propagate so the
        // durable dispatcher can retry / dead-letter. Command paths
        // (StartAsync / ResumeAsync / link) use PreserveRunning and
        // absorb the same failure, leaving the epic running-but-idle —
        // covered by RecomputeProgressAsync_CommandPath_TryStartFromEpicAsyncThrows_LeavesEpicRunning.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory) { ThrowOnStart = true };
        var grain = grains.GetEpicGrain("project_1:1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.RecomputeProgressAsync());

        Assert.NotEmpty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_CommandPath_TryStartFromEpicAsyncThrows_LeavesEpicRunning()
    {
        // Command paths (StartAsync, ResumeAsync, link) use
        // StartFailureMode.PreserveRunning. TryStartFromEpicAsync failures are
        // caught and logged; the epic remains running-but-idle so the
        // next event-driven recompute can re-evaluate. Here we drive
        // ResumeAsync as a representative command path.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory) { ThrowOnStart = true };
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("running", result.Status);
        Assert.NotEmpty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ResumeAsync_StartFailure_PersistsRecoveryEvent()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var eventStore = new RecordingEventStore();
        var grains = new RecordingGrainFactory(database.Factory, eventStore) { ThrowOnStart = true };
        var grain = grains.GetEpicGrain("project_1:1");

        var resumed = await grain.ResumeAsync();

        Assert.Equal("running", resumed.Status);
        var failure = Assert.Single(eventStore.Appended,
            evt => evt.Envelope.Type == EventCatalog.ReverseDns.EpicStartAttemptFailed);
        ProducerConformance.Assert(
            EventProducerFamily.Epic,
            failure.Envelope.Extensions,
            new(ProjectId: "project_1", Epic: "1"));
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().SingleAsync();
        Assert.Equal("running", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ResumeAsync_PausedEpic_AdvancesAfterResume()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Done, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("running", result.Status);
        Assert.Null(result.PauseReason);
        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:2", started);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task ResumeAsync_AlreadyRunningEpic_IsIdempotentAndDoesNotReAdvance()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.InProgress, canStart: true);
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true);

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("running", result.Status);
        Assert.Empty(grains.IssueStartCalls);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task PauseAsync_AlreadyPausedEpic_IsIdempotentNoOp()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused", pauseReason: "on hold");
        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var result = await grain.PauseAsync("re-paused");

        Assert.Equal("paused", result.Status);
        Assert.Equal("on hold", result.PauseReason);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("paused", stored.Status);
        Assert.Equal("on hold", stored.PauseReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task PauseAsync_IdleEpic_ThrowsEpicPauseRequiresRunning()
    {
        // The grain lets EpicPauseRequiresRunningException propagate so
        // the HTTP layer can map it to 409 EPIC_NOT_RUNNING. Pause only
        // short-circuits (no-op) at the domain level when the epic is
        // already Paused — for Idle, the precondition isn't met and the
        // caller MUST be told.
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var ex = await Assert.ThrowsAsync<EpicPauseRequiresRunningException>(
            () => grain.PauseAsync("on hold"));
        Assert.Equal("idle", ex.CurrentStatus);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task PauseAsync_DoneEpic_ThrowsEpicAlreadyTerminal()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        var ex = await Assert.ThrowsAsync<EpicAlreadyTerminalException>(
            () => grain.PauseAsync("on hold"));
        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("paused", ex.RequestedStatus);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Epic)]
    [Fact]
    public async Task RecomputeProgressAsync_HighestPriorityStartableWins()
    {
        var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 1, status: IssueStatus.Backlog, canStart: true, priority: "p3");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 2, status: IssueStatus.Backlog, canStart: true, priority: "p0");
        await SeedIssueAsync(database, projectId: "project_1", epicNumber: 1, issueNumber: 3, status: IssueStatus.Backlog, canStart: true, priority: "p1");

        var grains = new RecordingGrainFactory(database.Factory);
        var grain = grains.GetEpicGrain("project_1:1");

        await grain.RecomputeProgressAsync();

        var started = Assert.Single(grains.IssueStartCalls);
        Assert.Equal("project_1:2", started);
    }

    private static async Task SeedEpicAsync(
        TestDatabase database,
        string projectId = "project_1",
        int number = 1,
        string status = "idle",
        string? pauseReason = null)
    {
        await using var db = database.CreateDbContext();
        db.Epics.Add(new EpicRow
        {
            ProjectId = projectId,
            Number = number,
            Title = $"Epic {number}",
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
        string projectId,
        int epicNumber,
        int issueNumber,
        Mohist.Server.Issue.Domain.IssueStatus status,
        bool canStart = false,
        string priority = "p2",
        int[]? prerequisiteNumbers = null)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            Priority = priority,
            IsDraft = !canStart,
            PrerequisiteNumbers = prerequisiteNumbers ?? [],
            EpicNumber = epicNumber,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            EpicNumber = epicNumber,
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

    /// <summary>
    /// Test double that records guarded Issue starts requested by Epic progression.
    /// </summary>
    private sealed class RecordingGrainFactory : IGrainFactory
    {
        private readonly IDbContextFactory<MohistDbContext> _dbFactory;
        private readonly IEventStore _eventStore;
        public List<string> IssueStartCalls { get; } = [];
        public bool ThrowOnStart { get; set; }

        public RecordingGrainFactory(IDbContextFactory<MohistDbContext> dbFactory, IEventStore? eventStore = null)
        {
            _dbFactory = dbFactory;
            _eventStore = eventStore ?? new NoopEventStore();
        }

        public IEpicGrain GetEpicGrain(string grainKey) =>
            new EpicGrain(
                _dbFactory,
                this,
                new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
                _eventStore,
                NullLogger<EpicGrain>.Instance) { GrainKeyForTest = grainKey };

        public IIssueGrain GetIssueGrain(string issueKey) => new RecordingIssueGrain(this, issueKey);

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
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
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
        public RecordingIssueGrain(RecordingGrainFactory owner, string issueKey)
        {
            _owner = owner;
            IssueKey = issueKey;
        }

        public string IssueKey { get; }

        public Task<int> CreateAsync(string projectId, int number, string title, string? body, IReadOnlyDictionary<string, string>? labels, string? priority, string? repositoryRef = null, string? risk = null, bool isDraft = false, string[]? attachmentIds = null, string? workflowProfileId = null, int[]? prerequisiteNumbers = null)
            => throw new NotSupportedException();
        public async Task<string> StartWorkAsync(Mohist.Server.Issue.Grains.WorkflowProjectContext? project = null)
        {
            _owner.IssueStartCalls.Add(IssueKey);
            if (_owner.ThrowOnStart)
                throw new InvalidOperationException("simulated start failure");
            return "wr_test";
        }
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
        public Task<bool> AssignEpicAsync(int epicNumber) => throw new NotSupportedException();
        public Task<bool> RemoveEpicAsync(int expectedEpicNumber) => throw new NotSupportedException();
        public Task<bool> TryStartFromEpicAsync(int expectedEpicNumber)
        {
            _owner.IssueStartCalls.Add(IssueKey);
            if (_owner.ThrowOnStart)
                throw new InvalidOperationException("simulated start failure");
            return Task.FromResult(true);
        }
    }
}
