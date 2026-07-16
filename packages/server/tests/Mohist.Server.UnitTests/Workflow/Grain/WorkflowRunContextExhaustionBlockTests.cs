using System.Text.Json;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class WorkflowRunContextExhaustionBlockTests
{
    [Fact]
    public void BlockStageWithContextExhaustion_OverwritesTaskFailureWithContextExhaustion()
    {
        var def = BuildDefinition();
        var run = WorkflowRun.Create("wr-block-1", def, DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(def.Stages[0].Tasks, def.Stages[0].Checks, DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", TestTime.UtcNow);
        run.StartTask("work-1", "runner-1", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "compile error"), DateTimeOffset.UnixEpoch);
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

    [Fact]
    public void BlockStageWithContextExhaustion_WithoutPercent_StillRecordsBlockingMessage()
    {
        var def = BuildDefinition();
        var run = WorkflowRun.Create("wr-block-2", def, DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(def.Stages[0].Tasks, def.Stages[0].Checks, DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-2", TestTime.UtcNow);
        run.StartTask("work-2", "runner-2", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "boom"), DateTimeOffset.UnixEpoch);

        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: null, sessionId: null);

        Assert.Equal(FailureReason.ContextExhaustion, run.Failure?.Reason);
        Assert.NotNull(run.Failure?.Message);
        Assert.Contains("Compact", run.Failure!.Message!);
    }

    [Fact]
    public void BlockStageWithContextExhaustion_AfterBlock_RetryThrows()
    {
        var def = BuildDefinition();
        var run = WorkflowRun.Create("wr-block-3", def, DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(def.Stages[0].Tasks, def.Stages[0].Checks, DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-3", TestTime.UtcNow);
        run.StartTask("work-3", "runner-3", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "boom"), DateTimeOffset.UnixEpoch);
        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: 95d, sessionId: null);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = run.Retry(DateTimeOffset.UnixEpoch));
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

    [Fact]
    public void ClearContextExhaustionFailure_AfterBlock_RewritesReasonToTaskFailedAndPreservesTaskId()
    {
        var run = WorkflowRun.Create("wr-block-4", BuildDefinition(), DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(BuildDefinition().Stages[0].Tasks, BuildDefinition().Stages[0].Checks, DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-1", TestTime.UtcNow);
        run.StartTask("work-1", "runner-1", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "compile error"), DateTimeOffset.UnixEpoch);
        run.BlockStageWithContextExhaustion(taskId: "task-1.1", contextUsagePercent: 95d, sessionId: null);

        var cleared = run.ClearContextExhaustionFailure();
        Assert.True(cleared);
        Assert.Equal(FailureReason.TaskFailed, run.Failure?.Reason);
        Assert.Equal("task-1.1", run.Failure?.TaskId);
        Assert.Equal(FailureReason.TaskFailed, run.CurrentStage().Failure?.Reason);

        var events = run.Retry(DateTimeOffset.UnixEpoch);
        Assert.NotEmpty(events);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void ClearContextExhaustionFailure_WithoutContextExhaustionReason_IsNoOp()
    {
        var def = BuildDefinition();
        var run = WorkflowRun.Create("wr-block-5", def, DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage(def.Stages[0].Tasks, def.Stages[0].Checks, DateTimeOffset.UnixEpoch);
        run.AssignTo("runner-5", TestTime.UtcNow);
        run.StartTask("work-5", "runner-5", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "compile error"), DateTimeOffset.UnixEpoch);
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
