using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class WorkflowStatusMapperFrontendStatusTests
{
    [Theory]
    [InlineData("Created", "created")]
    [InlineData("AwaitingBinding", "awaiting-binding")]
    [InlineData("Pending", "pending")]
    [InlineData("Ready", "ready")]
    [InlineData("Running", "running")]
    [InlineData("AwaitingApproval", "awaiting-approval")]
    [InlineData("Paused", "paused")]
    [InlineData("Stopped", "stopped")]
    [InlineData("Completed", "completed")]
    [InlineData("Failed", "failed")]
    public void FrontendStatus_EmitsLowercaseForAllEnumValues(string raw, string expected)
    {
        var result = WorkflowStatusMapper.FrontendStatus(raw);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FrontendStatus_ForCreated_EmitsCreated()
    {
        Assert.Equal("created", WorkflowStatusMapper.FrontendStatus(WorkflowRunStatus.Created.ToString()));
    }

    [Fact]
    public void FrontendStatus_ForReady_EmitsReady()
    {
        Assert.Equal("ready", WorkflowStatusMapper.FrontendStatus(WorkflowRunStatus.Ready.ToString()));
    }

    [Fact]
    public void FrontendStatus_ForPending_EmitsPending()
    {
        Assert.Equal("pending", WorkflowStatusMapper.FrontendStatus(WorkflowRunStatus.Pending.ToString()));
    }

    [Fact]
    public void FrontendStatus_ForAwaitingApproval_EmitsHyphenatedForm()
    {
        Assert.Equal("awaiting-approval", WorkflowStatusMapper.FrontendStatus(WorkflowRunStatus.AwaitingApproval.ToString()));
    }

    [Fact]
    public void BuildStatusView_ForReady_ProjectsReadyStatus()
    {
        var run = CreateRun(WorkflowRunStatus.Ready);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view);
        Assert.Equal("ready", view!.Status);
    }

    [Fact]
    public void BuildStatusView_ForCreated_ProjectsCreatedStatus()
    {
        var run = CreateRun(WorkflowRunStatus.Created);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view);
        Assert.Equal("created", view!.Status);
    }

    [Fact]
    public void BuildStatusView_ForPending_ProjectsPendingStatus()
    {
        var run = CreateRun(WorkflowRunStatus.Pending);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view);
        Assert.Equal("pending", view!.Status);
    }

    [Fact]
    public void BuildStatusView_ForAwaitingApproval_ProjectsHyphenatedStatus()
    {
        var run = CreateRun(WorkflowRunStatus.AwaitingApproval);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view);
        Assert.Equal("awaiting-approval", view!.Status);
    }

    [Fact]
    public void BuildStatusView_ForPaused_ProjectsPausedStatus()
    {
        var run = CreateRun(WorkflowRunStatus.Paused);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view);
        Assert.Equal("paused", view!.Status);
    }

    private static WorkflowRun CreateRun(WorkflowRunStatus status) =>
        new()
        {
            Id = "wf-frontend",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = status,
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Pending,
                    Tasks = [],
                    Checks = []
                }
            ]
        };
}

public class WorkflowStatusMapperBuildPendingWorkTests
{
    [Fact]
    public void BuildPendingWork_ForReadyRun_WithDispatchableTask_ReturnsTask()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Ready, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.NotNull(pending);
        Assert.Equal("task", pending!.WorkType);
        Assert.Equal("build.1", pending.WorkId);
    }

    [Fact]
    public void BuildPendingWork_ForRunningRun_WithDispatchableTask_ReturnsTask()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Running, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.NotNull(pending);
        Assert.Equal("task", pending!.WorkType);
        Assert.Equal("build.1", pending.WorkId);
    }

    [Fact]
    public void BuildPendingWork_ForReadyRun_WithNoCurrentStageId_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Ready, TaskRunStatus.Pending);
        run.CurrentStageId = null;

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForReadyRun_WithAllTasksCompleted_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Ready, TaskRunStatus.Completed);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForRunningRun_WithAllTasksCompleted_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Running, TaskRunStatus.Completed);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForCreatedRun_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Created, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForPendingRun_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Pending, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForPausedRun_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Paused, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForAwaitingApprovalRun_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.AwaitingApproval, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForCompletedRun_ReturnsNull()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Completed, TaskRunStatus.Pending);

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.Null(pending);
    }

    [Fact]
    public void BuildPendingWork_ForReadyRun_WithUnpassedChecks_ReturnsChecksPending()
    {
        var run = CreateRunWithTask(WorkflowRunStatus.Ready, TaskRunStatus.Completed);
        run.Stages[0].Checks =
        [
            new StageCheck
            {
                Name = "health",
                Title = "Health check",
                Uses = "core/script",
                Status = StageCheckStatus.Failed
            }
        ];

        var pending = WorkflowStatusMapper.BuildPendingWork(run);

        Assert.NotNull(pending);
        Assert.Equal("checks", pending!.WorkType);
    }

    private static WorkflowRun CreateRunWithTask(WorkflowRunStatus runStatus, TaskRunStatus taskStatus) =>
        new()
        {
            Id = "wf-pending",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = runStatus,
            CurrentStageId = "build",
            Stages =
            [
                new StageRun
                {
                    Id = "build",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = "build.1",
                            DefinitionId = "build",
                            Attempt = 1,
                            Title = "Build step",
                            Status = taskStatus,
                            Uses = "core/script",
                            Classification = TaskClassification.UserFacing
                        }
                    ],
                    Checks = []
                }
            ]
        };
}
