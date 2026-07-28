using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Epic.Domain;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Issue.Domain;
using Mohist.Server.SpecTests.Support;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class EpicAutoDoneSpecs
{
    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_IdleEpicWithAllDoneIssues_TransitionsToDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Done);

        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("done", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_AlreadyDoneEpic_IsIdempotentNoOp()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("done", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_AlreadyClosedEpic_IsIdempotentNoOp()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "closed");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("closed", result!.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_PausedEpic_IsIdempotentNoOp()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused", pauseReason: "on hold");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("paused", result!.Status);
        Assert.Equal("on hold", result.PauseReason);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("paused", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_IdleEpicWithIncompleteIssue_StaysIdle()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_MixedDoneAndCancelledLinkedIssues_TransitionsToDone()
    {
        // After the terminal/open rule change, every linked issue is
        // terminal (issue_1 done, issue_2 cancelled) — no open linked
        // issue remains, so the epic auto-marks done. deliveredCount
        // still counts only the done issue; cancelled is terminal for
        // readiness but not counted as delivered.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("done", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_UnknownEpic_ReturnsNull()
    {
        await using var database = CreateDatabase();
        var grain = CreateGrain(database.Factory, "project_1:9999");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_NoLinkedIssues_TransitionsToDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("done", result!.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_DuplicateCallsOnAlreadyDone_AllReturnDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var first = await grain.AutoMarkDoneIfReadyAsync();
        var second = await grain.AutoMarkDoneIfReadyAsync();
        var third = await grain.AutoMarkDoneIfReadyAsync();

        Assert.Equal("done", first!.Status);
        Assert.Equal("done", second!.Status);
        Assert.Equal("done", third!.Status);
    }

    [Fact]
    public async Task ResumeAsync_PausedEpicWithAllCompleteIssues_TransitionsThroughRunningAndEndsDone()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("done", result.Status);
        Assert.Null(result.PauseReason);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
        Assert.Null(stored.PauseReason);
    }

    [Fact]
    public async Task ResumeAsync_PausedEpicWithIncompleteIssue_EndsRunning()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 2, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("running", result.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("running", stored.Status);
    }

    [Fact]
    public async Task ResumeAsync_PausedEpicWithCancelledIssueOnly_AutoDoneAfterResume()
    {
        // Cancelled is a terminal state, so an epic whose only linked
        // issue is cancelled has no open linked issues. Resume re-evaluates
        // via the shared readiness rule and auto-transitions to done.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Cancelled);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("done", result.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("done", stored.Status);
    }

    [Fact]
    public async Task ResumeAsync_RunningEpicIsNoOpAndStaysRunning()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.ResumeAsync();

        Assert.Equal("running", result.Status);
    }

    [Fact]
    public async Task ResumeAsync_IdleEpic_ThrowsEpicResumeRequiresPaused()
    {
        // Resume only short-circuits (no-op) at the domain level when
        // the epic is already Running. For Idle, the precondition
        // isn't met and the exception propagates so the HTTP layer
        // can map it to 409 EPIC_RESUME_REQUIRES_PAUSED.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var ex = await Assert.ThrowsAsync<EpicResumeRequiresPausedException>(
            () => grain.ResumeAsync());
        Assert.Equal("idle", ex.CurrentStatus);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Fact]
    public async Task ResumeAsync_DoneEpic_ThrowsEpicAlreadyTerminal()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var ex = await Assert.ThrowsAsync<EpicAlreadyTerminalException>(
            () => grain.ResumeAsync());
        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Fact]
    public async Task StartAsync_DoneEpic_ThrowsEpicAlreadyTerminal()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var ex = await Assert.ThrowsAsync<EpicAlreadyTerminalException>(
            () => grain.StartAsync());
        Assert.Equal("done", ex.CurrentStatus);
        Assert.Equal("running", ex.RequestedStatus);
    }

    [Fact]
    public async Task StartAsync_PausedEpic_ThrowsEpicStartRequiresIdle()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused", pauseReason: "on hold");
        var grain = CreateGrain(database.Factory, "project_1:1");

        var ex = await Assert.ThrowsAsync<EpicStartRequiresIdleException>(
            () => grain.StartAsync());
        Assert.Equal("paused", ex.CurrentStatus);
    }

    [Fact]
    public async Task SetStatusAsync_Done_StillThrowsOnTerminalOrPausedEpic_RegressionCheck()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "done");
        var grain = CreateGrain(database.Factory, "project_1:1");

        await Assert.ThrowsAnyAsync<Exception>(() => grain.SetStatusAsync("done"));
    }

    [Fact]
    public async Task SetStatusAsync_Done_StillThrowsOnPausedEpic_RegressionCheck()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "paused");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Done);
        var grain = CreateGrain(database.Factory, "project_1:1");

        await Assert.ThrowsAnyAsync<Exception>(() => grain.SetStatusAsync("done"));
    }

    [Fact]
    public async Task SetStatusAsync_Done_StillThrowsOnUnreadyEpic_RegressionCheck()
    {
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        await Assert.ThrowsAnyAsync<Exception>(() => grain.SetStatusAsync("done"));
    }

    [Fact]
    public async Task SetStatusAsync_Done_EpicWithOpenLinkedIssue_ThrowsNotReadyToMarkDoneAndStatusUnchanged()
    {
        // Invariant 2: explicit MarkDone must reject when any open
        // linked issue exists. SetStatusAsync("done") is the user
        // entry point; it must surface EpicNotReadyToMarkDoneException
        // and leave the status unchanged. This is the symmetric pin
        // to wake-up: open issues exhausted -> auto-done; new open
        // issue linked -> wake. The flip side: an explicit MarkDone
        // with open issues never silently flips to done.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "running");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var ex = await Assert.ThrowsAsync<EpicNotReadyToMarkDoneException>(
            () => grain.SetStatusAsync("done"));
        Assert.Equal(1, ex.EpicNumber);
        Assert.Equal(1, ex.OpenLinkedCount);

        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("running", stored.Status);
    }

    [Fact]
    public async Task AutoMarkDoneIfReadyAsync_EpicWithOpenLinkedIssue_IsNoOpAndStatusUnchanged()
    {
        // Invariant 2 (auto path): AutoMarkDoneIfReadyAsync must be a
        // no-op when at least one open linked issue exists. The epic
        // must not transition to done; status and event history stay
        // untouched. Pairs with the explicit MarkDone rejection above
        // to close the status-mirrors-reality loop.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.Backlog);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.AutoMarkDoneIfReadyAsync();

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    [Fact]
    public async Task RecomputeProgressAsync_EpicWithOpenLinkedIssue_DoesNotMarkDoneAndStatusUnchanged()
    {
        // Invariant 2 (recompute-progress-on-terminal-event path): the auto
        // recompute that fires when a linked issue reaches terminal
        // must NOT flip the epic to done while an open linked issue
        // remains. With status idle and an open linked issue, the
        // recompute path leaves the epic idle (no TryStartNextAsync
        // is invoked because status != running) and never marks done.
        await using var database = CreateDatabase();
        await SeedEpicAsync(database, status: "idle");
        await SeedIssueAsync(database, projectId: "project_1", issueNumber: 1, status: Mohist.Server.Issue.Domain.IssueStatus.InProgress);
        var grain = CreateGrain(database.Factory, "project_1:1");

        var result = await grain.RecomputeProgressAsync();

        Assert.NotNull(result);
        Assert.Equal("idle", result!.Status);
        await using var verify = database.CreateDbContext();
        var stored = await verify.Epics.AsNoTracking().FirstAsync();
        Assert.Equal("idle", stored.Status);
    }

    private static EpicGrain CreateGrain(TestDbContextFactory factory, string grainKey)
    {
        var identity = GrainTestContext.Create(grainKey);
        return new EpicGrain(
            identity.Context,
            identity.Runtime,
            factory,
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero)),
            new NoopEventStore(),
            NullLogger<EpicGrain>.Instance);
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
        int issueNumber,
        Mohist.Server.Issue.Domain.IssueStatus status)
    {
        var issue = new Mohist.Server.Issue.Domain.Issue
        {
            ProjectId = projectId,
            Number = issueNumber,
            Title = $"Issue {issueNumber}",
            Status = status,
            EpicNumber = 1,
        };
        var json = IssueStore.Serialize(issue);
        await using var db = database.CreateDbContext();
        db.Issues.Add(new IssueRow
        {
            ProjectId = projectId,
            Number = issueNumber,
            EpicNumber = 1,
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
}
