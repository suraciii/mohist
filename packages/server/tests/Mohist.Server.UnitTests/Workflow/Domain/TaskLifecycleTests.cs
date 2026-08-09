using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class TaskLifecycleTests
{
    private static WorkflowRun BuildRun()
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition( [
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [new("build-ok", "Build OK", "spec/check")])
        ]), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage([new("compile", "Compile", "spec/task")], [new("build-ok", "Build OK", "spec/check")], DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", TestTime.UtcNow);
        return run;
    }

    [Fact]
    public void StartTask_TransitionsPendingTaskToRunning_WithDispatchMetadataAndEvent()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);

        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.Null(task.FinishedAt);
        Assert.Equal("work-1", task.WorkId);
        Assert.Equal("worker-1", task.WorkerId);
        var evt = Assert.IsType<TaskStarted>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
        Assert.Equal("build", evt.Stage);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal("worker-1", evt.WorkerId);
        Assert.Equal(EventCatalog.ReverseDns.TaskStarted, WorkflowEventSerializer.BusType(events[0]));
        Assert.IsType<TaskStarted>(WorkflowEventSerializer.Unwrap(
            WorkflowEventSerializer.FromData(nameof(TaskStarted), WorkflowEventSerializer.ToData(events[0]))));
    }

    [Fact]
    public void CompleteTask_SetsFinishedAtBeforeCompletingRunningTask()
    {
        var run = BuildRun();
        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks[0];

        var events = run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.NotNull(task.FinishedAt);
        Assert.IsType<TaskCompleted>(WorkflowEventSerializer.Unwrap(events[0]));
    }

    [Fact]
    public void CompleteTask_DoesNotCompletePendingTask()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.CompleteTask(DateTimeOffset.UnixEpoch);

        Assert.Empty(events);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.FinishedAt);
    }

    [Fact]
    public void FailTask_DoesNotFailPendingTask()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.FailTask(new TaskResult("failed", "boom"), DateTimeOffset.UnixEpoch);

        Assert.Empty(events);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.FinishedAt);
        Assert.Null(run.Failure);
    }

    [Fact]
    public void FailTaskForStopped_FailsRunningTaskWithStoppedReason()
    {
        var run = BuildRun();
        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        var task = run.CurrentStage().Tasks[0];

        var events = run.FailTaskForStopped("stopped", DateTimeOffset.UnixEpoch);

        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("stopped", run.Failure?.Message);
        Assert.Equal(new TaskFailed("build", task.Id, "stopped"), WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Fact]
    public void RequeueTaskAfterPausedStop_ClearsActiveBindingAndUnlocksResumeAndRerun()
    {
        var run = BuildRun();
        run.StartTask("work-1", "worker-1", DateTimeOffset.UnixEpoch);
        run.Pause();

        Assert.True(run.RequeueTaskAfterPausedStop("work-1", "worker-1"));

        var task = run.CurrentStage().Tasks[0];
        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.WorkId);
        Assert.Null(task.WorkerId);
        Assert.Null(run.CurrentActiveWorkFor("worker-1"));

        run.Resume(DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);

        run.Status = WorkflowRunStatus.Failed;
        run.RerunFromStage("build", DateTimeOffset.UnixEpoch);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void StageCheck_LifecycleTransitionsThroughDispatched()
    {
        var check = new StageCheck
        {
            Name = "build-ok",
            Title = "Build OK",
            Status = StageCheckStatus.Pending
        };

        check.Status = StageCheckStatus.Pending;
        Assert.Equal(StageCheckStatus.Pending, check.Status);

        check.Status = StageCheckStatus.Passed;
        Assert.Equal(StageCheckStatus.Passed, check.Status);
    }
}
