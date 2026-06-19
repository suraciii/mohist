using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.Workflow.Grain;

/// <summary>
/// Unit tests for the <see cref="WorkflowRunExtensions.BlockStageWithContextExhaustion"/>
/// domain helper. The helper rewrites the current stage's failure to
/// <see cref="FailureReason.ContextExhaustion"/>, persists the
/// blocking message, and emits the same set of events the regular
/// failure path emits so downstream listeners (status mapper, event
/// store) see a consistent failed-stage transition.
/// </summary>
public class WorkflowRunContextExhaustionBlockSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BlockStageWithContextExhaustion_OverwritesTaskFailureWithContextExhaustion()
    {
        var run = WorkflowRun.Create("wr-block-1", BuildDefinition());
        run.Start();

        // Simulate a previous task failure.
        run.FailStage("compile error");
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);

        var events = run.BlockStageWithContextExhaustion(
            taskId: "task-1.1",
            contextUsagePercent: 92d,
            sessionId: "agent-1");

        Assert.Equal(FailureReason.ContextExhaustion, run.Failure?.Reason);
        Assert.Equal("task-1.1", run.Failure?.TaskId);
        Assert.Equal("build", run.Failure?.Stage);
        Assert.NotNull(run.Failure?.Message);
        Assert.Contains("92", run.Failure!.Message!);
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);

        var current = run.CurrentStage();
        Assert.Equal(StageRunStatus.Failed, current.Status);
        Assert.Same(run.Failure, current.Failure);

        Assert.Contains(events, e => e is StageFailed);
        Assert.Contains(events, e => e is WorkflowRunFailed);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BlockStageWithContextExhaustion_WithoutPercent_StillRecordsBlockingMessage()
    {
        var run = WorkflowRun.Create("wr-block-2", BuildDefinition());
        run.Start();
        run.FailStage("boom");

        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: null, sessionId: null);

        Assert.Equal(FailureReason.ContextExhaustion, run.Failure?.Reason);
        Assert.NotNull(run.Failure?.Message);
        Assert.Contains("Compact", run.Failure!.Message!);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void BlockStageWithContextExhaustion_AfterBlock_RetryThrows()
    {
        // The retry path is what users hit when they click "Retry" on
        // a stage that is now blocked by context exhaustion. The retry
        // helper must refuse (throw) and the status mapper must not
        // surface a retry action.
        var run = WorkflowRun.Create("wr-block-3", BuildDefinition());
        run.Start();
        run.FailStage("boom");
        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: 95d, sessionId: null);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = run.Retry());
        Assert.Contains("context exhaustion", ex.Message, StringComparison.OrdinalIgnoreCase);

        var status = WorkflowStatusMapper.BuildStatusView(run, null);
        Assert.NotNull(status);
        Assert.NotNull(status!.Failure);
        Assert.Equal("ContextExhaustion", status.Failure!.Reason);

        var retry = status.AvailableActions.Find(a => a.Name == "retry");
        Assert.Null(retry);

        var compact = status.AvailableActions.Find(a => a.Name == "compact");
        Assert.NotNull(compact);
        var reset = status.AvailableActions.Find(a => a.Name == "reset");
        Assert.NotNull(reset);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ClearContextExhaustionFailure_AfterBlock_RewritesReasonToTaskFailedAndPreservesTaskId()
    {
        // The grain calls ClearContextExhaustionFailure when the user
        // recovers the session context. The retry path can then call
        // Retry() without throwing and re-run the original task.
        var run = WorkflowRun.Create("wr-block-4", BuildDefinition());
        run.Start();
        run.InitializeStage(BuildDefinition().Stages[0].Tasks, BuildDefinition().Stages[0].Checks);
        run.StartTask("work-block", "runner-block");
        run.FailTask(new TaskResult("failed", "compile error"));
        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: 95d, sessionId: null);

        var cleared = run.ClearContextExhaustionFailure();
        Assert.True(cleared);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("task-1.1", run.Failure?.TaskId);
        Assert.Equal(FailureReason.TaskFailed, run.CurrentStage().Failure?.Reason);

        // Retry() now goes through the TaskFailed path and resumes the
        // workflow without throwing.
        var events = run.Retry();
        Assert.NotEmpty(events);
        Assert.Equal(WorkflowRunStatus.Running, run.Status);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.Workflow)]
    [Fact]
    public void ClearContextExhaustionFailure_WithoutContextExhaustionReason_IsNoOp()
    {
        // The grain should not touch a failure that is not a context
        // exhaustion block (e.g. regular TaskFailed). The demotion
        // helper is a no-op in that case.
        var run = WorkflowRun.Create("wr-block-5", BuildDefinition());
        run.Start();
        run.FailStage("compile error");
        var original = run.Failure;
        Assert.Equal(FailureReason.TaskFailed, original?.Reason);

        var cleared = run.ClearContextExhaustionFailure();
        Assert.False(cleared);
        Assert.Same(original, run.Failure);
    }

    private static WorkflowDefinition BuildDefinition() => new(
        "spec/workflow",
        [
            new StageDefinition("build",
                [new("task-1", "Task 1", "spec/task")],
                [new("check-1", "Check 1", "spec/check")])
        ]);
}
