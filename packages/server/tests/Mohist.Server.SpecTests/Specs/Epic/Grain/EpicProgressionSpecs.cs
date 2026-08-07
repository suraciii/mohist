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
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Epic.Grain;

public class EpicProgressionSpecs : EpicProgressionTestSupport
{
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

}
