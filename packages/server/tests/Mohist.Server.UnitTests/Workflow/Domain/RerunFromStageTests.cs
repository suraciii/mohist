using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class RerunFromStageTests
{
    private static WorkflowDefinition ThreeStageDefinition() =>
        new([
            new StageDefinition("plan",
                [new("draft", "Draft", "spec/task")],
                [new("plan-ok", "Plan OK", "spec/check")]),
            new StageDefinition("build",
                [new("compile", "Compile", "spec/task")],
                [new("build-ok", "Build OK", "spec/check")]),
            new StageDefinition("integrate",
                [new("merge", "Merge", "spec/task")],
                [new("merge-ok", "Merge OK", "spec/check")]),
        ]);

    private static WorkflowRun BuildRunAtStage(string currentStageId)
    {
        var run = WorkflowRun.Create("wf-1", ThreeStageDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        var stageIdx = run.Stages.FindIndex(s => s.Id == currentStageId);
        for (var i = 0; i <= stageIdx; i++)
        {
            var stage = run.Stages[i];
            var def = ThreeStageDefinition().Stages[i];
            run.InitializeStage(def.Tasks, def.Checks, DateTimeOffset.UnixEpoch);
            if (run.Assignment is null)
                run.AssignTo("worker-1", TestTime.UtcNow);
            if (i == stageIdx)
                break;

            run.StartTask(stage.Tasks.Single().Id, "worker-1", DateTimeOffset.UnixEpoch);
            run.CompleteTask(DateTimeOffset.UnixEpoch);
            run.PassCheck(new CheckResult(stage.Checks.Single().Name, CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);
        }
        return run;
    }

    private static WorkflowRun BuildCompletedRun()
    {
        var run = WorkflowRun.Create("wf-1", ThreeStageDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);

        var plan = run.CurrentStage();
        run.InitializeStage(
            [new("draft", "Draft", "spec/task")],
            [new("plan-ok", "Plan OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        run.StartTask("draft.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("plan-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);

        var buildStage = run.Stages[1];
        run.CurrentStageId = buildStage.Id;
        buildStage.Status = StageRunStatus.Running;
        run.InitializeStage(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.StartTask("compile.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("build-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);

        var integrateStage = run.Stages[2];
        run.CurrentStageId = integrateStage.Id;
        integrateStage.Status = StageRunStatus.Running;
        run.InitializeStage(
            [new("merge", "Merge", "spec/task")],
            [new("merge-ok", "Merge OK", "spec/check")],
            DateTimeOffset.UnixEpoch);
        run.StartTask("merge.1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);
        run.PassCheck(new CheckResult("merge-ok", CheckResultStatus.Passed), DateTimeOffset.UnixEpoch);

        return run;
    }

    [Fact]
    public void RerunFromStage_TargetStageReplacedWithNewAttempt_LaterStagesReset()
    {
        var run = BuildCompletedRun();
        var originalPlan = run.Stages[0];
        var originalBuild = run.Stages[1];
        var originalIntegrate = run.Stages[2];

        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var events = run.RerunFromStage("build", DateTimeOffset.UnixEpoch);

        // Target stage replaced
        var newBuild = run.Stages[1];
        Assert.Equal("build", newBuild.Id);
        Assert.Equal(originalBuild.Attempt + 1, newBuild.Attempt);
        Assert.False(newBuild.Initialized);
        Assert.Empty(newBuild.Tasks);
        Assert.Empty(newBuild.Checks);
        Assert.Equal(StageRunStatus.Running, newBuild.Status);
        Assert.Equal(originalBuild.RequiresApproval, newBuild.RequiresApproval);

        // Later stages keep a new attempt when their prior attempt was initialized.
        var newIntegrate = run.Stages[2];
        Assert.Equal("integrate", newIntegrate.Id);
        Assert.Equal(originalIntegrate.Attempt + 1, newIntegrate.Attempt);
        Assert.False(newIntegrate.Initialized);
        Assert.Empty(newIntegrate.Tasks);
        Assert.Empty(newIntegrate.Checks);
        Assert.Equal(StageRunStatus.Pending, newIntegrate.Status);

        // Stages before target untouched
        var preservedPlan = run.Stages[0];
        Assert.Equal(originalPlan.Id, preservedPlan.Id);
        Assert.Equal(originalPlan.Attempt, preservedPlan.Attempt);
        Assert.Equal(originalPlan.Initialized, preservedPlan.Initialized);
        Assert.Equal(originalPlan.Tasks.Count, preservedPlan.Tasks.Count);
        Assert.Equal(originalPlan.Checks.Count, preservedPlan.Checks.Count);
    }

    [Fact]
    public void RerunFromStage_SetsCurrentStageIdClearsFailureSetsRunning()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var events = run.RerunFromStage("build", DateTimeOffset.UnixEpoch);

        Assert.Equal("build", run.CurrentStageId);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(2, events.Count);
        Assert.True(events[0] is WorkflowRunResumed,
            "First event should be WorkflowRunResumed");
        Assert.True(events[1] is StageStarted started && started.Stage == "build",
            "Second event should be StageStarted for build");
    }

    [Fact]
    public void RerunFromStage_UnknownStage_ThrowsBeforeMutation()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("nonexistent", DateTimeOffset.UnixEpoch));

        Assert.Equal("unknown_stage", ex.Code);
        Assert.NotNull(run.Failure);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal("integrate", run.CurrentStageId);
    }

    [Fact]
    public void RerunFromStage_NeverReachedStage_ThrowsWithEligibleStages()
    {
        var run = BuildRunAtStage("build");
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "build", "compile.1");

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("integrate", DateTimeOffset.UnixEpoch));

        Assert.Equal("stage_not_reached", ex.Code);

        if (ex.Details is not null)
        {
            var detailsJson = ex.Details;
            using var doc = JsonDocument.Parse(detailsJson);
            Assert.True(doc.RootElement.TryGetProperty("eligibleStages", out var eligible));
            var stageIds = eligible.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("plan", stageIds);
            Assert.Contains("build", stageIds);
            Assert.DoesNotContain("integrate", stageIds);
        }

        Assert.Equal("build", run.CurrentStageId);
    }

    [Fact]
    public void RerunFromStage_PreviouslyReachedLaterStage_RejectedAfterBackwardRerunUntilProgressCatchesUp()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        run.RerunFromStage("plan", DateTimeOffset.UnixEpoch);
        var stagesAfterPlanRerun = run.Stages.ToList();

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("integrate", DateTimeOffset.UnixEpoch));

        Assert.Equal("stage_not_reached", ex.Code);
        Assert.Equal("plan", run.CurrentStageId);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Same(stagesAfterPlanRerun[0], run.Stages[0]);
        Assert.Same(stagesAfterPlanRerun[1], run.Stages[1]);
        Assert.Same(stagesAfterPlanRerun[2], run.Stages[2]);
    }

    [Fact]
    public void RerunFromStage_CurrentInitializedStageIsEligible()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        run.RerunFromStage("integrate", DateTimeOffset.UnixEpoch);

        Assert.Equal("integrate", run.CurrentStageId);
        Assert.Null(run.Failure);
        Assert.Equal(2, run.Stages.Single(s => s.Id == "integrate").Attempt);
    }

    [Fact]
    public void RerunFromStage_ActiveTaskInRange_ThrowsWithActiveWorkCode()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        // Add a running task to the integrate stage (which is in the target-to-end range when targeting build)
        var integrateStage = run.Stages[2];
        var runningTask = new TaskRun
        {
            Id = "merge-active",
            DefinitionId = "merge",
            Attempt = 1,
            Title = "Active Merge",
            Uses = "spec/task",
            Status = TaskRunStatus.Running,
        };
        integrateStage.Tasks.Add(runningTask);

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("build", DateTimeOffset.UnixEpoch));

        Assert.Equal("active_work_in_range", ex.Code);
        Assert.NotNull(run.Failure);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Fact]
    public void RerunFromStage_PendingCheckInFailedRange_IsDiscardedByNewAttempt()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        // Add a pending check to the build stage (which is the target)
        var buildStage = run.Stages[1];
        buildStage.Checks.Add(new StageCheck
        {
            Name = "pending-check",
            Title = "Pending Check",
            Uses = "spec/check",
            Status = StageCheckStatus.Pending,
        });

        run.RerunFromStage("build", DateTimeOffset.UnixEpoch);

        var newBuild = run.Stages.Single(s => s.Id == "build");
        Assert.Empty(newBuild.Checks);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void RerunFromStage_PendingTaskInFailedRange_IsDiscarded()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        // Add a pending (never-started) task to a later stage — it should not
        // block rerun since it was never dispatched.
        var integrateStage = run.Stages[2];
        integrateStage.Tasks.Add(new TaskRun
        {
            Id = "merge-pending",
            DefinitionId = "merge",
            Attempt = 1,
            Title = "Pending Merge",
            Uses = "spec/task",
            Status = TaskRunStatus.Pending,
        });

        var events = run.RerunFromStage("build", DateTimeOffset.UnixEpoch);

        var newIntegrate = run.Stages.Single(s => s.Id == "integrate");
        Assert.Empty(newIntegrate.Tasks);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void RerunFromStage_RunningCheckInRange_ThrowsWithActiveWorkCode()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var buildStage = run.Stages[1];
        buildStage.Checks.Add(new StageCheck
        {
            Name = "running-check",
            Title = "Running Check",
            Uses = "spec/check",
            Status = StageCheckStatus.Running,
        });

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("build", DateTimeOffset.UnixEpoch));

        Assert.Equal("active_work_in_range", ex.Code);
        Assert.NotNull(run.Failure);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
    }

    [Fact]
    public void RerunFromStage_ActiveTaskInTargetStageItself_Throws()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        // Add a running task to the target stage (build)
        var buildStage = run.Stages[1];
        var runningTask = new TaskRun
        {
            Id = "build-active",
            DefinitionId = "compile",
            Attempt = 1,
            Title = "Active Build",
            Uses = "spec/task",
            Status = TaskRunStatus.Running,
        };
        buildStage.Tasks.Add(runningTask);

        var ex = Assert.Throws<WorkflowControlRejectionException>(() =>
            run.RerunFromStage("build", DateTimeOffset.UnixEpoch));

        Assert.Equal("active_work_in_range", ex.Code);
        Assert.NotNull(run.Failure);
    }

    [Fact]
    public void RerunFromStage_CleanRange_Succeeds()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var events = run.RerunFromStage("build", DateTimeOffset.UnixEpoch);

        Assert.Equal("build", run.CurrentStageId);
        Assert.Null(run.Failure);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void RerunFromStage_RerunFromPlan_ResetsBuildAndIntegrate()
    {
        var run = BuildCompletedRun();
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        var events = run.RerunFromStage("plan", DateTimeOffset.UnixEpoch);

        // Target is plan
        Assert.Equal("plan", run.CurrentStageId);
        var newPlan = run.Stages[0];
        Assert.Equal(2, newPlan.Attempt);

        // Build and integrate are fresh and use their next attempts.
        Assert.Equal(2, run.Stages[1].Attempt);
        Assert.False(run.Stages[1].Initialized);
        Assert.Equal(StageRunStatus.Pending, run.Stages[1].Status);

        Assert.Equal(2, run.Stages[2].Attempt);
        Assert.False(run.Stages[2].Initialized);
        Assert.Equal(StageRunStatus.Pending, run.Stages[2].Status);
    }

    [Fact]
    public void RerunFromStage_ReinitializedTaskUsesStageScopedIdentity()
    {
        var run = BuildCompletedRun();
        var originalTaskId = run.Stages.Single(stage => stage.Id == "build").Tasks.Single().Id;
        run.Status = WorkflowRunStatus.Failed;
        run.Failure = new FailureDetails(FailureReason.TaskFailed, "integrate", "merge.1");

        run.RerunFromStage("build", DateTimeOffset.UnixEpoch);
        run.InitializeStage(
            [new("compile", "Compile", "spec/task")],
            [new("build-ok", "Build OK", "spec/check")],
            DateTimeOffset.UnixEpoch,
            advance: false);

        var rerunTask = run.CurrentStage().Tasks.Single();
        Assert.Equal("compile.s2.1", rerunTask.Id);
        Assert.NotEqual(originalTaskId, rerunTask.Id);
    }
}
