using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class WorkflowRunStatusTransitionTests
{
    private const string WorkerId = "worker-1";

    [Fact]
    public void WorkflowRunStatus_ValuesMatchSingleWaitingObjectVocabulary()
    {
        Assert.Equal(
            [
                nameof(WorkflowRunStatus.Created),
                nameof(WorkflowRunStatus.Pending),
                nameof(WorkflowRunStatus.Ready),
                nameof(WorkflowRunStatus.Running),
                nameof(WorkflowRunStatus.AwaitingApproval),
                nameof(WorkflowRunStatus.Paused),
                nameof(WorkflowRunStatus.Stopped),
                nameof(WorkflowRunStatus.Completed),
                nameof(WorkflowRunStatus.Failed)
            ],
            Enum.GetNames<WorkflowRunStatus>());
    }

    [Fact]
    public void Start_MovesCreatedToPending()
    {
        var run = CreateRun();

        var events = run.Start(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Ready, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Running, run.Status);
        Assert.IsType<WorkflowRunStarted>(WorkflowEventSerializer.Unwrap(events[0]));
        Assert.IsType<StageStarted>(WorkflowEventSerializer.Unwrap(events[1]));
    }

    [Fact]
    public void AssignWorker_MovesPendingToReady_AndStoresWorkerOnAssignment()
    {
        var run = BuildPendingRun();

        run.AssignTo(WorkerId, TestTime.UtcNow);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(WorkerId, run.Assignment!.WorkerId);
    }

    [Fact]
    public void StartTask_MovesReadyToRunning()
    {
        var run = BuildReadyRun();

        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks.Single().Status);
    }

    [Fact]
    public void CompleteTask_WithRemainingDispatchableWork_ReturnsAssignedRunToReady()
    {
        var run = BuildReadyRun(tasks:
        [
            new("compile", "Compile", "spec/task"),
            new("test", "Test", "spec/task")
        ], checks: []);
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(TaskRunStatus.Pending, run.CurrentStage().Tasks[1].Status);
    }

    [Fact]
    public void CompleteTask_WithRemainingDispatchableWork_ReturnsUnassignedRunToPending()
    {
        var run = BuildReadyRun(tasks:
        [
            new("compile", "Compile", "spec/task"),
            new("test", "Test", "spec/task")
        ], checks: []);
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);
        run.Assignment = null;

        run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        Assert.Equal(TaskRunStatus.Pending, run.CurrentStage().Tasks[1].Status);
    }

    [Fact]
    public void CompleteTask_WithNoRemainingWork_CompletesRun()
    {
        var run = BuildReadyRun(checks: []);
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
    }

    [Fact]
    public void Pause_AdmitsPendingReadyAndRunningStates()
    {
        var pending = BuildPendingRun();
        var ready = BuildReadyRun();
        var running = BuildReadyRun();
        running.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        pending.Pause();
        ready.Pause();
        running.Pause();

        Assert.Equal(WorkflowRunStatus.Paused, pending.Status);
        Assert.Equal(WorkflowRunStatus.Paused, ready.Status);
        Assert.Equal(WorkflowRunStatus.Paused, running.Status);
    }

    [Fact]
    public void Resume_WithInFlightWork_ReturnsToRunning()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);
        run.Pause();

        run.Resume(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Fact]
    public void Resume_WithDispatchableWorkAndNoInFlightWork_ReturnsToReady()
    {
        var run = BuildReadyRun();
        run.Pause();

        run.Resume(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void Approve_MovesAwaitingApprovalToReadyForNextStage()
    {
        var run = BuildAwaitingApprovalRun();

        var events = run.Approve(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal("build", run.CurrentStageId);
        Assert.Contains(events, e => WorkflowEventSerializer.Unwrap(e) is StageStarted { Stage: "build" });
    }

    [Fact]
    public void FailTask_LandsOnFailed()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        run.FailTask(new TaskResult("failed", "boom"), DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Fact]
    public void Stop_LandsOnStopped()
    {
        var run = BuildReadyRun();

        run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
    }

    [Fact]
    public void Stop_FromAwaitingApproval_ClearsCurrentStageApprovalGate()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        Assert.True(current.IsAwaitingApproval);
        Assert.NotNull(current.ApprovalStatus);
        Assert.Equal(StageRunStatus.AwaitingApproval, current.Status);

        var events = run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Null(current.ApprovalStatus);
        Assert.NotEqual(StageRunStatus.AwaitingApproval, current.Status);
        Assert.Equal(StageRunStatus.Running, current.Status);
        _ = events;
    }

    [Fact]
    public void Stop_FromRunningStage_LeavesApprovalStatusUnchanged()
    {
        var run = BuildReadyRun();
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);
        var current = run.CurrentStage();
        Assert.False(current.IsAwaitingApproval);
        Assert.Null(current.ApprovalStatus);
        Assert.Equal(StageRunStatus.Running, current.Status);

        run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Null(current.ApprovalStatus);
        Assert.NotEqual(StageRunStatus.AwaitingApproval, current.Status);
    }

    [Fact]
    public void Stop_EmitsOnlyWorkflowRunStopped_WhenClearingAwaitingApprovalGate()
    {
        // spec requirement 3 / scenario: stop is termination, not an approval
        // decision. No StageApprovalResolved event must accompany the gate
        // cleanup, even when the current stage was awaiting approval.
        var run = BuildAwaitingApprovalRun();

        var events = run.Stop();
        var unwrapped = events.Select(WorkflowEventSerializer.Unwrap).ToList();

        Assert.Contains(unwrapped, e => e is WorkflowRunStopped);
        Assert.DoesNotContain(unwrapped, e => e is StageApprovalResolved);
        Assert.DoesNotContain(unwrapped, e => e is StageApprovalRequested);
    }

    [Fact]
    public void ClearStaleApprovalGate_CorrectsPersistedDirtyRun()
    {
        var run = BuildAwaitingApprovalRun();
        run.Status = WorkflowRunStatus.Stopped;
        var current = run.CurrentStage();
        Assert.NotNull(current.ApprovalStatus);
        Assert.True(current.IsAwaitingApproval);

        var changed = run.ClearStaleApprovalGate();

        Assert.True(changed);
        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.Null(current.ApprovalStatus);
        Assert.False(current.IsAwaitingApproval);
        Assert.NotEqual(StageRunStatus.AwaitingApproval, current.Status);
        Assert.Equal(StageRunStatus.Running, current.Status);
    }

    [Fact]
    public void ClearStaleApprovalGate_IsIdempotentOnAlreadyCleanRun()
    {
        var run = BuildAwaitingApprovalRun();
        run.Status = WorkflowRunStatus.Stopped;
        var current = run.CurrentStage();
        Assert.True(run.ClearStaleApprovalGate());

        var changed = run.ClearStaleApprovalGate();

        Assert.False(changed);
        Assert.Null(current.ApprovalStatus);
        Assert.Equal(StageRunStatus.Running, current.Status);
    }

    [Fact]
    public void ClearStaleApprovalGate_ClearsGateOnLiveAwaitingApprovalRun()
    {
        var run = BuildAwaitingApprovalRun();
        var current = run.CurrentStage();
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.True(current.IsAwaitingApproval);

        var changed = run.ClearStaleApprovalGate();

        Assert.True(changed);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        Assert.Null(current.ApprovalStatus);
        Assert.Equal(StageRunStatus.Running, current.Status);
    }

    [Fact]
    public void Reject_LandsOnFailed()
    {
        var run = BuildAwaitingApprovalRun();

        run.Reject("not enough detail", DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    // Regression for issue #387: HasInFlightWork must only look at the
    // current stage. A completed prior stage may carry a stale
    // ChecksWorkId (left behind by a stage that has since advanced); that
    // stale id does not mean work is in flight NOW, and treating it as such
    // leaves the run on Running when it should fall to Ready, making it
    // invisible to the worker's Ready/Pending-only dispatch query.
    [Fact]
    public void HasInFlightWork_IgnoresStaleChecksWorkIdOnCompletedStages()
    {
        var run = WorkflowRun.Create("wr_387", new WorkflowDefinition("spec/wf", [
            new StageDefinition("build",
                [new("compile", "Compile", "spec/t")],
                [new("verify", "Verify", "spec/check")]),
            new StageDefinition("integrate",
                [new("merge", "Merge", "spec/t")],
                [])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("compile", "Compile", "spec/t")],
            [new("verify", "Verify", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo(WorkerId, TestTime.UtcNow);
        run.StartTask("w-compile", WorkerId, DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("verify", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        // Build has advanced; integrate is now current.
        Assert.Equal("integrate", run.CurrentStageId);
        run.InitializeStage([new("merge", "Merge", "spec/t")], [], DateTimeOffset.UnixEpoch);

        // Inject a stale ChecksWorkId on the now-completed build stage,
        // simulating the leak observed in production. The current stage
        // (integrate) has only pending work and nothing running.
        run.Stages[0].ChecksWorkId = "checks-build:leaked";

        Assert.False(run.HasInFlightWork(),
            "stale ChecksWorkId on a completed stage must not count as in-flight work");
    }

    [Theory]
    [InlineData(CheckResultStatus.Passed)]
    [InlineData(CheckResultStatus.Failed)]
    [InlineData(CheckResultStatus.Pending)]
    public void CheckReport_ClearsCurrentStageChecksWorkId(CheckResultStatus checkStatus)
    {
        var run = BuildReadyRun(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]);
        // Simulate a dispatched check batch: ChecksWorkId set while Running.
        run.CurrentStage().ChecksWorkId = "checks-build:abc";
        run.CurrentStage().Checks[0].Status = StageCheckStatus.Running;

        var result = new CheckResult("build-ok", checkStatus);
        switch (checkStatus)
        {
            case CheckResultStatus.Passed:
                run.PassCheck(result, DateTimeOffset.UnixEpoch);
                break;
            case CheckResultStatus.Failed:
                run.FailCheck(result, DateTimeOffset.UnixEpoch);
                break;
            case CheckResultStatus.Pending:
                run.ResetCheck(result, DateTimeOffset.UnixEpoch);
                break;
        }

        Assert.Null(run.CurrentStage().ChecksWorkId);
    }

    // Offer/claim two-phase dispatch invariant (issue #387 follow-up):
    // NextWork() is the offer — it must NOT mutate run state. The task stays
    // Pending and the run stays Ready until a claim (StartTask) explicitly
    // transitions it. This is what makes "Running ⟹ claimed" a flow
    // invariant: there is no window where a task is Running without a worker
    // having durably registered it.
    [Fact]
    public void NextWork_OfferDoesNotMutateRunState()
    {
        var run = BuildReadyRun(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]);

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        var taskBefore = run.CurrentStage().Tasks[0];
        Assert.Equal(TaskRunStatus.Pending, taskBefore.Status);

        // Offer: NextWork returns the pending task but must not start it.
        var offered = run.NextWork();

        Assert.NotNull(offered);
        Assert.Equal(TaskRunStatus.Pending, taskBefore.Status);
        Assert.Null(taskBefore.WorkerId);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void StartTask_ClaimTransitionsPendingToRunning()
    {
        var run = BuildReadyRun(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")]);

        run.NextWork(); // offer (no state change)
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        // Claim: StartTask is what transitions the task to Running.
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);

        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks[0].Status);
        Assert.Equal(WorkerId, run.CurrentStage().Tasks[0].WorkerId);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    // Offer-then-crash recovery: if the worker takes the offer but crashes
    // before claiming, the task is still Pending and the run is still Ready.
    // A subsequent offer (new poll) re-offers the same work. No reconcile
    // is needed because offer never persisted any "in-flight" state.
    [Fact]
    public void NextWork_OfferIsRepeatableBeforeClaim()
    {
        var run = BuildReadyRun(
            [new("compile", "Compile", "spec/task")],
            []);

        var first = run.NextWork();
        var second = run.NextWork();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.GetType(), second!.GetType());
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(TaskRunStatus.Pending, run.CurrentStage().Tasks[0].Status);
    }

    private static WorkflowRun BuildAwaitingApprovalRun()
    {
        var run = WorkflowRun.Create("wr_approval", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("plan", [new("draft", "Draft", "spec/task")], [new("plan-ok", "Plan OK", "spec/check")], RequiresApproval: true),
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage([new("draft", "Draft", "spec/task")], [new("plan-ok", "Plan OK", "spec/check")], DateTimeOffset.UnixEpoch);
        run.AssignTo(WorkerId, TestTime.UtcNow);
        run.StartTask("work-1", WorkerId, DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        return run;
    }

    private static WorkflowRun BuildReadyRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        var run = BuildPendingRun(tasks, checks);
        run.AssignTo(WorkerId, TestTime.UtcNow);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        return run;
    }

    private static WorkflowRun BuildPendingRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        var taskList = tasks ?? [new("compile", "Compile", "spec/task")];
        var checkList = checks ?? [new("build-ok", "Build OK", "spec/check")];
        var run = CreateRun(taskList, checkList);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(taskList, checkList, DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
        return run;
    }

    private static WorkflowRun CreateRun(
        List<TaskDefinition>? tasks = null,
        List<CheckDefinition>? checks = null)
    {
        return WorkflowRun.Create("wr_1", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build",
                tasks ?? [new("compile", "Compile", "spec/task")],
                checks ?? [new("build-ok", "Build OK", "spec/check")])
        ]), DateTimeOffset.UnixEpoch);
    }
}
