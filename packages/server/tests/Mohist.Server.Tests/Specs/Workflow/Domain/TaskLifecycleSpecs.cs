using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Domain;

public class TaskLifecycleSpecs
{
    private static WorkflowRun BuildRun()
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [new("build-ok", "Build OK", "spec/check")])
        ]));
        run.Start();
        run.InitializeStage([new("compile", "Compile", "spec/task")], [new("build-ok", "Build OK", "spec/check")]);
        return run;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void StartTask_TransitionsPendingTaskToRunning_WithDispatchMetadataAndEvent()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.StartTask("work-1", "runner-1");

        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.Null(task.FinishedAt);
        Assert.Equal("work-1", task.WorkId);
        Assert.Equal("runner-1", task.RunnerId);
        var evt = Assert.IsType<TaskStarted>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
        Assert.Equal("build", evt.Stage);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal("runner-1", evt.RunnerId);
        Assert.Equal(EventCatalog.ReverseDns.TaskStarted, WorkflowEventSerializer.BusType(events[0]));
        Assert.IsType<TaskStarted>(WorkflowEventSerializer.Unwrap(
            WorkflowEventSerializer.FromData(nameof(TaskStarted), WorkflowEventSerializer.ToData(events[0]))));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CompleteTask_SetsFinishedAtBeforeCompletingRunningTask()
    {
        var run = BuildRun();
        run.StartTask("work-1", "runner-1");
        var task = run.CurrentStage().Tasks[0];

        var events = run.CompleteTask();

        Assert.Equal(TaskRunStatus.Completed, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.NotNull(task.FinishedAt);
        Assert.IsType<TaskCompleted>(WorkflowEventSerializer.Unwrap(events[0]));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void CompleteTask_DoesNotCompletePendingTask()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.CompleteTask();

        Assert.Empty(events);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.FinishedAt);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FailTask_DoesNotFailPendingTask()
    {
        var run = BuildRun();
        var task = run.CurrentStage().Tasks[0];

        var events = run.FailTask(new TaskResult("failed", "boom"));

        Assert.Empty(events);
        Assert.Equal(TaskRunStatus.Pending, task.Status);
        Assert.Null(task.FinishedAt);
        Assert.Null(run.Failure);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FailTaskForRunnerLost_FailsRunningTaskAndEmitsFailureSequence()
    {
        var run = BuildRun();
        run.StartTask("work-1", "runner-1");
        var task = run.CurrentStage().Tasks[0];

        var events = run.FailTaskForRunnerLost();

        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("runner-lost", run.Failure?.Message);
        Assert.Collection(events,
            e => Assert.Equal(new TaskFailed("build", task.Id, "runner-lost"), WorkflowEventSerializer.Unwrap(e)),
            e => Assert.Equal(new StageFailed("build", "runner-lost"), WorkflowEventSerializer.Unwrap(e)),
            e => Assert.Equal(new WorkflowRunFailed("runner-lost"), WorkflowEventSerializer.Unwrap(e)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FailTaskForStopped_FailsRunningTaskWithStoppedReason()
    {
        var run = BuildRun();
        run.StartTask("work-1", "runner-1");
        var task = run.CurrentStage().Tasks[0];

        var events = run.FailTaskForStopped("stopped");

        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.NotNull(task.FinishedAt);
        Assert.Equal("stopped", run.Failure?.Message);
        Assert.Equal(new TaskFailed("build", task.Id, "stopped"), WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void StageCheck_LifecycleTransitionsThroughDispatched()
    {
        var check = new StageCheck
        {
            Name = "build-ok",
            Title = "Build OK",
            Status = StageCheckStatus.Pending
        };

        check.Status = StageCheckStatus.Dispatched;
        Assert.Equal(StageCheckStatus.Dispatched, check.Status);

        check.Status = StageCheckStatus.Passed;
        Assert.Equal(StageCheckStatus.Passed, check.Status);
    }
}
