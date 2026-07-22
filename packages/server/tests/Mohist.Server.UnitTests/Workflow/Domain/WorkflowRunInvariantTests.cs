using Mohist.Server.Infrastructure.Events;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class WorkflowRunInvariantTests
{
    private static WorkflowRun BuildRun(bool requiresApproval = false, bool assign = true)
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition( [
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [],
                RequiresApproval: requiresApproval)
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage([new("compile", "Compile", "spec/task")], [], DateTimeOffset.UnixEpoch);
        if (assign)
            run.AssignTo("worker-1", TestTime.UtcNow);
        return run;
    }

    private static WorkflowRun BuildMultiTaskRun()
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition( [
            new StageDefinition("build", [new("compile", "Compile", "spec/task"), new("test", "Test", "spec/task")], [])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage([new("compile", "Compile", "spec/task"), new("test", "Test", "spec/task")], [], DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        return run;
    }

    [Fact]
    public void SecondAssignmentRejectedWhenOneExists()
    {
        var run = BuildRun(assign: false);
        run.AssignTo("worker-1", TestTime.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            run.AssignTo("worker-2", TestTime.UtcNow));

        Assert.Contains("already assigned", ex.Message);
        Assert.True(run.IsAssignedTo("worker-1"));
        Assert.Equal("worker-1", run.Assignment!.WorkerId);
        Assert.False(run.IsAssignedTo("worker-2"));
    }

    [Fact]
    public void RunningTaskWorkerIdEqualsAssignmentWorkerId()
    {
        var run = BuildRun(assign: false);
        run.AssignTo("worker-1", TestTime.UtcNow);

        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks[0];

        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(run.Assignment!.WorkerId, task.WorkerId);
        Assert.Equal("worker-1", task.WorkerId);
    }

    [Fact]
    public void ReadyRunWithNoRunningTaskHasNoInFlightWork()
    {
        var run = BuildRun();

        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.All(run.CurrentStage().Tasks, t => Assert.Equal(TaskRunStatus.Pending, t.Status));
        Assert.DoesNotContain(run.CurrentStage().Tasks, t => t.Status == TaskRunStatus.Running);
    }

    [Fact]
    public void PausedOnlyResultsFromWorkflowLevelCommand()
    {
        var run = BuildRun();
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        var events = run.Pause();

        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.IsType<WorkflowRunPaused>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Fact]
    public void StoppedOnlyResultsFromWorkflowLevelCommand()
    {
        var run = BuildRun();
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        var events = run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.IsType<WorkflowRunStopped>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Fact]
    public void AwaitingApprovalResultsFromWorkflowApprovalGate()
    {
        var run = BuildRun(requiresApproval: true);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var events = run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        var approvalEvent = Assert.IsType<StageApprovalRequested>(WorkflowEventSerializer.Unwrap(events[^1]));
        Assert.Equal("build", approvalEvent.Stage);
    }

    [Fact]
    public void TaskCompletionDoesNotDeriveWorkflowStatus()
    {
        var run = BuildMultiTaskRun();
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.NotEqual(WorkflowRunStatus.Paused, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Stopped, run.Status);
        Assert.NotEqual(WorkflowRunStatus.AwaitingApproval, run.Status);
    }

    [Fact]
    public void FailTaskIsPolicyReactionNotStatusDerivation()
    {
        var run = BuildRun();
        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks[0];
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        var events = run.FailTask(new TaskResult("failed", "task error"), DateTimeOffset.UnixEpoch);

        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.NotEmpty(events);
        // The workflow transition to Failed is a one-shot policy reaction
        // triggered by this specific event, not a continuous recomputation:
        // there is no Status = f(task statuses) path. After this call,
        // the run is Failed regardless of other task states — confirming
        // it's an event-driven decision, not a status derivation.
    }

    [Fact]
    public void NonTerminalTaskTransitionDoesNotRecomputeWorkflowStatus()
    {
        var run = BuildMultiTaskRun();
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks[0].Status);

        run.CompleteTask(DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
        Assert.Equal(TaskRunStatus.Completed, run.CurrentStage().Tasks[0].Status);
    }
}
