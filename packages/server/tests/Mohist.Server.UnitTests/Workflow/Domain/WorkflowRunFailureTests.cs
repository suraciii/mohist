using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public class WorkflowRunFailureTests
{
    [Fact]
    public void RetryTarget_ResolvesLegacyContextExhaustionToFailedTask()
    {
        var run = FailedTaskRun();
        var task = run.CurrentStage().Tasks.Single();
        run.Failure = run.CurrentStage().Failure = new FailureDetails(
            FailureReason.ContextExhaustion,
            "build");

        var target = run.RetryTarget();

        Assert.NotNull(target);
        Assert.Equal(FailureReason.TaskFailed, target!.Reason);
        Assert.Equal(task.Id, target.Target);
        var retry = WorkflowStatusMapper.BuildAvailableActions(run);
        Assert.Equal(task.Id, Assert.Single(retry, action => action.Name == "retry").Target);
    }

    [Fact]
    public void Retry_UsesResolvedLegacyTaskTarget()
    {
        var run = FailedTaskRun();
        var stage = run.CurrentStage();
        stage.Failure = run.Failure = new FailureDetails(FailureReason.ContextExhaustion, stage.Id);

        run.Retry(DateTimeOffset.UnixEpoch);

        Assert.Equal(2, stage.Tasks.Count);
        Assert.Equal(TaskRunStatus.Failed, stage.Tasks[0].Status);
        Assert.Equal(TaskRunStatus.Pending, stage.Tasks[1].Status);
        Assert.Equal(WorkflowRunStatus.Ready, run.Status);
    }

    [Fact]
    public void Retry_UsesResolvedCheckTarget()
    {
        var run = new WorkflowRun
        {
            Id = "wr-check",
            Metadata = new WorkflowRunMetadata("check", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Failed,
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    Initialized = true,
                    RequiresApproval = false,
                    Status = StageRunStatus.Failed,
                    Tasks = [],
                    Checks =
                    [
                        new StageCheck { Name = "lint", Title = "Lint", Status = StageCheckStatus.Failed }
                    ],
                    Failure = new FailureDetails(FailureReason.CheckFailed, "build", CheckName: "lint")
                }
            ],
            Failure = new FailureDetails(FailureReason.CheckFailed, "build", CheckName: "lint")
        };

        run.Retry(DateTimeOffset.UnixEpoch);

        Assert.Equal(StageCheckStatus.Pending, Assert.Single(run.CurrentStage().Checks).Status);
        Assert.Equal(WorkflowRunStatus.Pending, run.Status);
    }

    [Fact]
    public void FailureWithoutRetryTarget_HidesRetryAndDoesNotMutateOnRetry()
    {
        var run = FailedTaskRun();
        var stage = run.CurrentStage();
        stage.Failure = run.Failure = new FailureDetails(FailureReason.ApprovalRejected, stage.Id);
        var beforeStatus = run.Status;
        var beforeTaskStatus = stage.Tasks.Single().Status;

        Assert.DoesNotContain(WorkflowStatusMapper.BuildAvailableActions(run), action => action.Name == "retry");
        Assert.Throws<InvalidOperationException>(() => run.Retry(DateTimeOffset.UnixEpoch));
        Assert.Equal(beforeStatus, run.Status);
        Assert.Equal(beforeTaskStatus, stage.Tasks.Single().Status);
    }

    private static WorkflowRun FailedTaskRun()
    {
        var run = WorkflowRun.Create(
            "wr-task",
            new WorkflowDefinition([new StageDefinition("build", [new("compile", "Compile", "spec/task")], [])]),
            DateTimeOffset.UnixEpoch);
        run.Start(DateTimeOffset.UnixEpoch);
        run.InitializeStage([new("compile", "Compile", "spec/task")], [], DateTimeOffset.UnixEpoch);
        run.AssignTo("worker-1", DateTimeOffset.UnixEpoch);
        run.StartTask("worker-1", "worker-1", DateTimeOffset.UnixEpoch);
        run.FailTask(new TaskResult("failed", "broken"), DateTimeOffset.UnixEpoch);
        return run;
    }
}
