using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.Tests.Specs.Workflow.Domain;

public class WorkflowRunInvariantSpecs
{
    private static WorkflowRun BuildRun(bool requiresApproval = false)
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build", [new("compile", "Compile", "spec/task")], [],
                RequiresApproval: requiresApproval)
        ]));
        run.Start();
        run.InitializeStage([new("compile", "Compile", "spec/task")], []);
        return run;
    }

    private static WorkflowRun BuildMultiTaskRun()
    {
        var run = WorkflowRun.Create("wr_1", new WorkflowDefinition("spec/workflow", [
            new StageDefinition("build", [new("compile", "Compile", "spec/task"), new("test", "Test", "spec/task")], [])
        ]));
        run.Start();
        run.InitializeStage([new("compile", "Compile", "spec/task"), new("test", "Test", "spec/task")], []);
        return run;
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void SecondClaimRejectedWhenOneExists()
    {
        var run = BuildRun();
        run.ClaimBy("runner-1", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            run.ClaimBy("runner-2", DateTimeOffset.UtcNow));

        Assert.Contains("already claimed", ex.Message);
        Assert.True(run.IsClaimedBy("runner-1"));
        Assert.Equal("runner-1", run.Claim!.RunnerId);
        Assert.False(run.IsClaimedBy("runner-2"));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void RunningTaskRunnerIdEqualsClaimRunnerId()
    {
        var run = BuildRun();
        run.ClaimBy("runner-1", DateTimeOffset.UtcNow);

        run.StartTask("work-1", "runner-1");
        var task = run.CurrentStage().Tasks[0];

        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(run.Claim!.RunnerId, task.RunnerId);
        Assert.Equal("runner-1", task.RunnerId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void RunningRunWithNoRunningTaskStaysRunning()
    {
        var run = BuildRun();

        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.All(run.CurrentStage().Tasks, t => Assert.Equal(TaskRunStatus.Pending, t.Status));
        Assert.DoesNotContain(run.CurrentStage().Tasks, t => t.Status == TaskRunStatus.Running);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void PausedOnlyResultsFromWorkflowLevelCommand()
    {
        var run = BuildRun();
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        var events = run.Pause();

        Assert.Equal(WorkflowRunStatus.Paused, run.Status);
        Assert.IsType<WorkflowRunPaused>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void StoppedOnlyResultsFromWorkflowLevelCommand()
    {
        var run = BuildRun();
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        var events = run.Stop();

        Assert.Equal(WorkflowRunStatus.Stopped, run.Status);
        Assert.IsType<WorkflowRunStopped>(WorkflowEventSerializer.Unwrap(Assert.Single(events)));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void AwaitingApprovalResultsFromWorkflowApprovalGate()
    {
        var run = BuildRun(requiresApproval: true);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        run.StartTask("work-1", "runner-1");
        var events = run.CompleteTask();

        Assert.Equal(WorkflowRunStatus.AwaitingApproval, run.Status);
        var approvalEvent = Assert.IsType<StageApprovalRequested>(WorkflowEventSerializer.Unwrap(events[^1]));
        Assert.Equal("build", approvalEvent.Stage);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void TaskCompletionDoesNotDeriveWorkflowStatus()
    {
        var run = BuildMultiTaskRun();
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        run.StartTask("work-1", "runner-1");
        run.CompleteTask();

        Assert.NotEqual(WorkflowRunStatus.Paused, run.Status);
        Assert.NotEqual(WorkflowRunStatus.Stopped, run.Status);
        Assert.NotEqual(WorkflowRunStatus.AwaitingApproval, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void FailTaskIsPolicyReactionNotStatusDerivation()
    {
        var run = BuildRun();
        run.StartTask("work-1", "runner-1");
        var task = run.CurrentStage().Tasks[0];
        Assert.Equal(TaskRunStatus.Running, task.Status);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        var events = run.FailTask(new TaskResult("failed", "task error"));

        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.NotEmpty(events);
        // The workflow transition to Failed is a one-shot policy reaction
        // triggered by this specific event, not a continuous recomputation:
        // there is no Status = f(task statuses) path. After this call,
        // the run is Failed regardless of other task states — confirming
        // it's an event-driven decision, not a status derivation.
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void NonTerminalTaskTransitionDoesNotRecomputeWorkflowStatus()
    {
        var run = BuildMultiTaskRun();
        Assert.Equal(WorkflowRunStatus.Running, run.Status);

        run.StartTask("work-1", "runner-1");
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(TaskRunStatus.Running, run.CurrentStage().Tasks[0].Status);

        run.CompleteTask();
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
        Assert.Equal(TaskRunStatus.Completed, run.CurrentStage().Tasks[0].Status);
    }
}
