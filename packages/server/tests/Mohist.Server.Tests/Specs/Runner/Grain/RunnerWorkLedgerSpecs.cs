using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Runner;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Tests.Specs.Workflow;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;
using Orleans;
using Xunit;

namespace Mohist.Server.Tests.Specs.Runner.Grain;

[Collection("WorkflowGrain")]
public class RunnerWorkLedgerSpecs : WorkflowGrainSpecs
{
    public RunnerWorkLedgerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task PollWorkflowWork_InsertsRunnerWorksRow_WithOutstandingStatusAndFakeTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var takeTime = _fixture.TimeProvider.GetUtcNow();

        var (work, runnerId) = await PollWorkAnyAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("outstanding", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);
        Assert.Null(row.Reason);
        Assert.Null(row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportWorkflowSuccess_UpdatesRowToCompleted_WithoutDeletingIt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        var finishedAt = _fixture.TimeProvider.GetUtcNow();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.Null(row.Reason);
        Assert.Equal(finishedAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReportWorkflowFailure_UpdatesRowToFailed_WithReasonAndFinishedAt_WithoutDeletingIt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        var finishedAt = _fixture.TimeProvider.GetUtcNow();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("failed", "it-broke"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.Equal("it-broke", row.Reason);
        Assert.Equal(finishedAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TerminalRow_IsNeverTransitionedAgain()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("failed", "first"));

        var firstTerminal = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("failed", firstTerminal!.Status);
        Assert.Equal("first", firstTerminal.Reason);

        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var stillTerminal = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("failed", stillTerminal!.Status);
        Assert.Equal("first", stillTerminal.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task Reactivation_HydratesOutstandingRows_FromLedger_PreservingTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        await DeactivateRunnerAsync(runnerId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        var state = await runner.GetRuntimeStateAsync();
        Assert.Contains(state.ActiveWorks, w =>
            w.OwnerKind == WorkDispatchOwnerKinds.Workflow
            && w.OwnerId == work.WorkflowRunId
            && w.WorkId == work.WorkId
            && w.TakenAt == takenAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task ReactivatedWorkflowWork_ReportPreservesOriginalTakenAt()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var takenAt = _fixture.TimeProvider.GetUtcNow();

        await DeactivateRunnerAsync(runnerId);
        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(5));

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.ReportWorkflowResultAsync(work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takenAt, row.TakenAt);
        Assert.NotEqual(takenAt, row.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task AgentJobWork_HasItsOnlyHomeInRunnerWorks()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-ledger-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-ledger-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-ledger-{Guid.NewGuid():N}";
        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        await job.SubmitAsync(new AgentJobInput(
            Prompt: "ledger test",
            WorkspacePath: "/tmp/agent-job-ledger",
            ProjectId: projectId));

        WorkDispatch? dispatch = await TestWait.ForAsync(
            () => runner.PollAsync(),
            d => d is not null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(20),
            $"Runner '{runnerId}' to receive dispatch for job {jobKey}");

        Assert.NotNull(dispatch);
        Assert.Equal(WorkDispatchOwnerKinds.AgentJob, dispatch!.OwnerKind);
        Assert.Equal(jobKey, dispatch.AgentJobId);

        var takeTime = _fixture.TimeProvider.GetUtcNow();
        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, dispatch.WorkId);
        Assert.NotNull(row);
        Assert.Equal("outstanding", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);

        _fixture.TimeProvider.Advance(TimeSpan.FromSeconds(5));
        var report = await runner.ReportAgentJobResultAsync(jobKey, dispatch.WorkId, new WorkResult("completed", "ok"));
        Assert.True(report.Tracked);

        row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, dispatch.WorkId);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(takeTime, row.TakenAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RunnerLoss_SynthesizesFailedRunnerLost_AndUpdatesLedgerRow()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("runner-lost", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("runner-lost", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task WorkCompletionTimeout_AfterRunnerGrainReactivation_DetectsOrphanWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        await DeactivateRunnerAsync(runnerId);
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        _fixture.TimeProvider.SetUtcNow(_fixture.TimeProvider.GetUtcNow().AddMinutes(11));
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", TestProjectId(work.WorkflowRunId)));
        await runner.CheckWorkTimeoutsAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task TimeoutThenRunnerLoss_DoesNotResynthesizeWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await AdvanceTimeKeepingRunnerOnlineAsync(runner, TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var runAfterTimeout = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("timeout", runAfterTimeout.Failure?.Message);

        await runner.UnregisterAsync();

        var runAfterLoss = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("timeout", runAfterLoss.Failure?.Message);
        Assert.Equal(TaskRunStatus.Failed, runAfterLoss.Stages.Single().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task RunnerLossThenTimeout_DoesNotResynthesizeWork()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.UnregisterAsync();

        Assert.Equal("Failed", await workflow.GetRunStatusAsync());
        var runAfterLoss = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("runner-lost", runAfterLoss.Failure?.Message);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        var runAfterTimeout = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal("runner-lost", runAfterTimeout.Failure?.Message);
        Assert.Equal(TaskRunStatus.Failed, runAfterTimeout.Stages.Single().Tasks.Single().Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task RunnerLoss_SynthesizesAgentJobFailure_AndUpdatesLedgerRow()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-loss-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-loss-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-loss-{Guid.NewGuid():N}";
        var work = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-job-loss-work-{Guid.NewGuid():N}",
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
        var assigned = await runner.AssignAgentJobAsync(work);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        await runner.UnregisterAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("runner-lost", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("runner-lost", terminal.Message);
        Assert.Equal("runner-lost", terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task WorkCompletionTimeout_SynthesizesWorkflowFailure_AndUpdatesLedgerRow()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();

        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await AdvanceTimeKeepingRunnerOnlineAsync(runner, TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var run = await LoadRunAsync(work.WorkflowRunId);
        Assert.Equal(TaskRunStatus.Failed, run.Stages.Single().Tasks.Single().Status);
        Assert.Equal("timeout", run.Failure?.Message);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Agent)]
    [Fact]
    public async Task WorkCompletionTimeout_SynthesizesAgentJobFailure_AndUpdatesLedgerRow()
    {
        await ClearBacklogAsync();
        var runnerId = $"agent-job-timeout-ledger-runner-{Guid.NewGuid():N}";
        var projectId = $"agent-job-timeout-ledger-project-{Guid.NewGuid():N}";
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.RegisterAsync(new RunnerInfo(runnerId, ["spec/*"], "test-host", projectId));

        var jobKey = $"agent-job-timeout-ledger-{Guid.NewGuid():N}";
        var work = new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"agent-job-timeout-work-{Guid.NewGuid():N}",
            AgentJobId: jobKey,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob);
        var assigned = await runner.AssignAgentJobAsync(work);
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(11));
        await runner.CheckWorkTimeoutsAsync();

        var row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, jobKey, work.WorkId);
        Assert.NotNull(row);
        Assert.Equal("failed", row!.Status);
        Assert.Equal("timeout", row.Reason);
        Assert.NotNull(row.FinishedAt);

        var job = Grains.GetGrain<IAgentJobGrain>(jobKey);
        var terminal = await job.GetTerminalResultAsync();
        Assert.Equal(AgentJobStatus.Failed, terminal.Status);
        Assert.Equal("timeout", terminal.Message);
        Assert.Equal("timeout", terminal.FailureReason);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task EnsureWorkTimeoutReminder_RegistersReminderOnlyOnFirstOutstandingWork()
    {
        // Reaches into the in-memory reminder table via the silo service
        // provider (the table is registered as a singleton by
        // UseInMemoryReminderService). The LocalReminderService starts a
        // local timer immediately on register, so a row appears in the
        // table the same moment a grain calls RegisterOrUpdateReminder.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (_, runnerId) = await PollWorkAnyAsync();

        var reminder = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(reminder);
        Assert.Equal(TimeSpan.FromMinutes(1), reminder!.Period);
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(1), reminder.StartAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task EnsureWorkTimeoutReminder_DoesNotResetStartAt_OnSubsequentAssignment()
    {
        // Pin the register-if-absent fix: the second work assignment while
        // the reminder already exists MUST NOT call RegisterOrUpdateReminder
        // (otherwise the row's StartAt would shift to now+period). The
        // table assertion is the precise, intent-revealing check for the
        // due-time-drift regression — it does not rely on the reminder
        // ticking, only on the row state.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (_, runnerId) = await PollWorkAnyAsync();
        var initial = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(initial);

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(2));

        var assigned = await Grains.GetGrain<IRunnerGrain>(runnerId).AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"extra-work-{Guid.NewGuid():N}",
            AgentJobId: $"extra-agent-{Guid.NewGuid():N}",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob));
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        var afterSecondAssignment = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(afterSecondAssignment);
        Assert.Equal(initial!.StartAt, afterSecondAssignment!.StartAt);
        Assert.Equal(initial.ETag, afterSecondAssignment.ETag);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task EnsureWorkTimeoutReminder_IsReleasedWhenOutstandingWorkDrains()
    {
        // Drain path: after every outstanding work has reported, the scan
        // observes no pending/running work and calls
        // MaybeUnregisterWorkTimeoutReminderAsync. The table must show no
        // reminder row for the runner grain id.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        Assert.NotNull(await GetWorkTimeoutReminderAsync(runnerId));

        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));

        Assert.Null(await GetWorkTimeoutReminderAsync(runnerId));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task EnsureWorkTimeoutReminder_ReregistersWithFreshStartAt_AfterDrainAndNewWork()
    {
        // After drain + new work, the reminder must come back with a
        // freshly-computed StartAt (register-if-absent semantics). The
        // row id parity here is implicit: a re-register after a
        // remove-then-add cycle gets a new StartAt equal to "now + period",
        // which is observably later than the pre-drain StartAt.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var preDrainStartAt = (await GetWorkTimeoutReminderAsync(runnerId))!.StartAt;

        await ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));
        Assert.Null(await GetWorkTimeoutReminderAsync(runnerId));

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(7));

        var assigned = await Grains.GetGrain<IRunnerGrain>(runnerId).AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: $"drained-reregister-work-{Guid.NewGuid():N}",
            AgentJobId: $"drained-reregister-agent-{Guid.NewGuid():N}",
            OwnerKind: WorkDispatchOwnerKinds.AgentJob));
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        var postAssign = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(postAssign);
        Assert.NotEqual(preDrainStartAt, postAssign!.StartAt);
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(1), postAssign.StartAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task EnsureWorkTimeoutReminder_RemainsRegistered_WhenNewWorkArrivesDuringUnregister()
    {
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var grainId = runner.GetGrainId();
        var initialReminder = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(initialReminder);

        var pause = _fixture.ControllableReminderTable.PauseNextRemove(grainId, "work-timeout");
        var reportTask = ReportAsync(runnerId, work.WorkflowRunId, work.WorkId, new WorkResult("completed"));
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(10));

        _fixture.TimeProvider.Advance(TimeSpan.FromMinutes(3));

        var newAgentJobId = $"unregister-race-agent-{Guid.NewGuid():N}";
        var newWorkId = $"unregister-race-work-{Guid.NewGuid():N}";
        var assigned = await runner.AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: newWorkId,
            AgentJobId: newAgentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob));
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        pause.Release();
        await reportTask.WaitAsync(TimeSpan.FromSeconds(10));

        var afterRace = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(afterRace);
        Assert.Equal(_fixture.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(1), afterRace!.StartAt);

        var newWorkRow = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, newAgentJobId, newWorkId);
        Assert.NotNull(newWorkRow);
        Assert.Equal("outstanding", newWorkRow!.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Grain)]
    [Trait(Traits.Sut.Name, Traits.Sut.Runner)]
    [Fact]
    public async Task OlderOutstandingWorkTimesOut_WhenNewerWorkIsAssignedBeforeItsDeadline()
    {
        // End-to-end behavioral pin: after the fix, assigning a new work
        // item W2 near W1's WorkCompletionTimeout deadline must NOT push
        // W1's judgment later. This test drives the behavior through the
        // actual reminder path (FakeTimeProvider drives the
        // LocalReminderService's PeriodicTimer, which fires
        // ReceiveReminder on the RunnerGrain), not only through
        // CheckWorkTimeoutsAsync.
        var workflow = await StartWorkflowAsync(SingleStage(checks: []));
        var (work, runnerId) = await PollWorkAnyAsync();
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);

        var initialReminder = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.NotNull(initialReminder);
        var initialStartAt = initialReminder!.StartAt;

        await AdvanceTimeKeepingRunnerOnlineAsync(runner, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));

        var w2AgentJobId = $"elder-agent-{Guid.NewGuid():N}";
        var w2WorkId = $"elder-work-mate-{Guid.NewGuid():N}";
        var assigned = await runner.AssignAgentJobAsync(new WorkDispatch(
            WorkflowRunId: string.Empty,
            WorkId: w2WorkId,
            AgentJobId: w2AgentJobId,
            OwnerKind: WorkDispatchOwnerKinds.AgentJob));
        Assert.Equal(RunnerWorkAssignmentStatus.Assigned, assigned.Status);

        var afterW2Reminder = await GetWorkTimeoutReminderAsync(runnerId);
        Assert.Equal(initialStartAt, afterW2Reminder!.StartAt);

        await AdvanceTimeKeepingRunnerOnlineAsync(runner, TimeSpan.FromMinutes(2));

        await WaitForRunStatusAsync(work.WorkflowRunId, "Failed", TimeSpan.FromSeconds(10));

        // The reminder tick and the synthesis path are asynchronous; the
        // workflow status reaches "Failed" before the ledger row is
        // updated by MarkRunnerWorkTerminalAsync. Poll the ledger row the
        // same way to avoid a flaky ordering assertion.
        var w1Row = await WaitForRunnerWorkStatusAsync(
            runnerId, WorkDispatchOwnerKinds.Workflow, work.WorkflowRunId, work.WorkId,
            expectedStatus: "failed", expectedReason: "timeout", TimeSpan.FromSeconds(10));
        Assert.Equal("timeout", w1Row!.Reason);

        var w2Row = await FindRunnerWorkAsync(runnerId, WorkDispatchOwnerKinds.AgentJob, w2AgentJobId, w2WorkId);
        Assert.NotNull(w2Row);
        Assert.Equal("outstanding", w2Row!.Status);
    }

    private async Task<RunnerWorkRow?> WaitForRunnerWorkStatusAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId,
        string expectedStatus,
        string? expectedReason,
        TimeSpan timeout)
    {
        return await TestWait.ForAsync(
            () => FindRunnerWorkAsync(runnerId, ownerKind, ownerId, workId),
            row => row is not null
                && string.Equals(row.Status, expectedStatus, StringComparison.Ordinal)
                && (expectedReason is null || string.Equals(row.Reason, expectedReason, StringComparison.Ordinal)),
            timeout,
            TimeSpan.FromMilliseconds(50),
            $"Runner work ({ownerKind}/{ownerId}/{workId}) to reach status '{expectedStatus}' (reason='{expectedReason}')");
    }

    private async Task<ReminderEntry?> GetWorkTimeoutReminderAsync(string runnerId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        var grainId = runner.GetGrainId();
        var table = await _fixture.ReminderTable.ReadRows(grainId);
        return table.Reminders.SingleOrDefault(r => r.ReminderName == "work-timeout");
    }

    private async Task AdvanceTimeKeepingRunnerOnlineAsync(IRunnerGrain runner, TimeSpan duration)
    {
        var remaining = duration;
        var step = TimeSpan.FromSeconds(30);
        while (remaining > TimeSpan.Zero)
        {
            var next = remaining < step ? remaining : step;
            _fixture.TimeProvider.Advance(next);
            await runner.HeartbeatAsync();
            remaining -= next;
        }
    }

    private async Task WaitForRunStatusAsync(
        string workflowId,
        string expectedStatus,
        TimeSpan timeout)
    {
        var workflow = Grains.GetGrain<IWorkflowGrain>(workflowId);
        await TestWait.ForAsync(
            () => workflow.GetRunStatusAsync(),
            status => string.Equals(status, expectedStatus, StringComparison.Ordinal),
            timeout,
            TimeSpan.FromMilliseconds(50),
            $"Workflow '{workflowId}' to reach status '{expectedStatus}'");
    }

    private async Task<RunnerWorkRow?> FindRunnerWorkAsync(
        string runnerId,
        string ownerKind,
        string ownerId,
        string workId)
    {
        await using var db = new MohistDbContext(new DbContextOptionsBuilder<MohistDbContext>()
            .UseSqlite(_fixture.ConnectionString)
            .Options);
        return await db.RunnerWorks
            .Where(r =>
                r.RunnerId == runnerId &&
                r.OwnerKind == ownerKind &&
                r.OwnerId == ownerId &&
                r.WorkId == workId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
    }

    private async Task DeactivateRunnerAsync(string runnerId)
    {
        var runner = Grains.GetGrain<IRunnerGrain>(runnerId);
        await runner.DeactivateForTestAsync();

        var management = Grains.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var activations = await management.GetDetailedGrainStatistics();
            if (!activations.Any(stat => stat.GrainType.Contains(nameof(RunnerGrain), StringComparison.Ordinal)
                && stat.GrainId.ToString()!.Contains(runnerId, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Runner grain '{runnerId}' did not deactivate in time.");
    }
}
