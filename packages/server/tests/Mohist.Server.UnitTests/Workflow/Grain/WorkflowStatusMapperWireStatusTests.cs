using System.Runtime.CompilerServices;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class WorkflowStatusMapperWireStatusTests
{
    public static IEnumerable<object[]> WorkflowRunStatusCases() =>
        Enum.GetValues<WorkflowRunStatus>()
            .Cast<WorkflowRunStatus>()
            .Select(s => new object[] { s, ExpectedWireFor(s.ToString()) });

    public static IEnumerable<object[]> StageRunStatusCases() =>
        Enum.GetValues<StageRunStatus>()
            .Cast<StageRunStatus>()
            .Select(s => new object[] { s, ExpectedWireFor(s.ToString()) });

    public static IEnumerable<object[]> TaskRunStatusCases() =>
        Enum.GetValues<TaskRunStatus>()
            .Cast<TaskRunStatus>()
            .Select(s => new object[] { s, ExpectedWireFor(s.ToString()) });

    public static IEnumerable<object[]> StageCheckStatusCases() =>
        Enum.GetValues<StageCheckStatus>()
            .Cast<StageCheckStatus>()
            .Select(s => new object[] { s, ExpectedWireFor(s.ToString()) });

    [Theory]
    [MemberData(nameof(WorkflowRunStatusCases))]
    public void WireStatus_WorkflowRunStatus_EmitsExpectedWireValue(WorkflowRunStatus status, string expected)
    {
        Assert.Equal(expected, WorkflowStatusMapper.WireStatus(status));
    }

    [Theory]
    [MemberData(nameof(StageRunStatusCases))]
    public void WireStatus_StageRunStatus_EmitsExpectedWireValue(StageRunStatus status, string expected)
    {
        Assert.Equal(expected, WorkflowStatusMapper.WireStatus(status));
    }

    [Theory]
    [MemberData(nameof(TaskRunStatusCases))]
    public void WireStatus_TaskRunStatus_EmitsExpectedWireValue(TaskRunStatus status, string expected)
    {
        Assert.Equal(expected, WorkflowStatusMapper.WireStatus(status));
    }

    [Theory]
    [MemberData(nameof(StageCheckStatusCases))]
    public void WireStatus_StageCheckStatus_EmitsExpectedWireValue(StageCheckStatus status, string expected)
    {
        Assert.Equal(expected, WorkflowStatusMapper.WireStatus(status));
    }

    [Fact]
    public void WireStatus_WorkflowRunStatus_AwaitingApproval_EmitsHyphenatedForm()
    {
        Assert.Equal("awaiting-approval", WorkflowStatusMapper.WireStatus(WorkflowRunStatus.AwaitingApproval));
    }

    [Fact]
    public void WireStatus_StageRunStatus_AwaitingApproval_EmitsHyphenatedForm()
    {
        Assert.Equal("awaiting-approval", WorkflowStatusMapper.WireStatus(StageRunStatus.AwaitingApproval));
    }

    [Fact]
    public void WireStatus_AllWireValues_AreKebabCase()
    {
        var samples = new[]
        {
            WorkflowStatusMapper.WireStatus(WorkflowRunStatus.AwaitingApproval),
            WorkflowStatusMapper.WireStatus(StageRunStatus.AwaitingApproval),
            WorkflowStatusMapper.WireStatus(StageCheckStatus.Passed),
            WorkflowStatusMapper.WireStatus(TaskRunStatus.Completed),
        };
        foreach (var value in samples)
        {
            Assert.Equal(value.ToLowerInvariant(), value);
            Assert.DoesNotContain(" ", value);
            Assert.True(value.All(c => char.IsLower(c) || c == '-'),
                $"Wire value '{value}' contains characters outside the kebab-case alphabet.");
        }
    }

    [Fact]
    public void WireStatus_WorkflowRunStatus_CoversEveryEnumValue()
    {
        var mapped = Enum.GetValues<WorkflowRunStatus>()
            .Select(WorkflowStatusMapper.WireStatus)
            .ToHashSet(StringComparer.Ordinal);
        var expected = Enum.GetValues<WorkflowRunStatus>()
            .Select(s => ExpectedWireFor(s.ToString()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void WireStatus_StageRunStatus_CoversEveryEnumValue()
    {
        var mapped = Enum.GetValues<StageRunStatus>()
            .Select(WorkflowStatusMapper.WireStatus)
            .ToHashSet(StringComparer.Ordinal);
        var expected = Enum.GetValues<StageRunStatus>()
            .Select(s => ExpectedWireFor(s.ToString()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void WireStatus_TaskRunStatus_CoversEveryEnumValue()
    {
        var mapped = Enum.GetValues<TaskRunStatus>()
            .Select(WorkflowStatusMapper.WireStatus)
            .ToHashSet(StringComparer.Ordinal);
        var expected = Enum.GetValues<TaskRunStatus>()
            .Select(s => ExpectedWireFor(s.ToString()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void WireStatus_StageCheckStatus_CoversEveryEnumValue()
    {
        var mapped = Enum.GetValues<StageCheckStatus>()
            .Select(WorkflowStatusMapper.WireStatus)
            .ToHashSet(StringComparer.Ordinal);
        var expected = Enum.GetValues<StageCheckStatus>()
            .Select(s => ExpectedWireFor(s.ToString()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void WireStatus_NoEnumHasDiscardOrToLowerInvariantFallback()
    {
        var enumValues = new[]
        {
            ("WorkflowRunStatus", Enum.GetValues<WorkflowRunStatus>().Length),
            ("StageRunStatus", Enum.GetValues<StageRunStatus>().Length),
            ("TaskRunStatus", Enum.GetValues<TaskRunStatus>().Length),
            ("StageCheckStatus", Enum.GetValues<StageCheckStatus>().Length),
        };
        var mappedCounts = new[]
        {
            ("WorkflowRunStatus", Enum.GetValues<WorkflowRunStatus>()
                .Select(s => WorkflowStatusMapper.WireStatus(s)).Distinct(StringComparer.Ordinal).Count()),
            ("StageRunStatus", Enum.GetValues<StageRunStatus>()
                .Select(s => WorkflowStatusMapper.WireStatus(s)).Distinct(StringComparer.Ordinal).Count()),
            ("TaskRunStatus", Enum.GetValues<TaskRunStatus>()
                .Select(s => WorkflowStatusMapper.WireStatus(s)).Distinct(StringComparer.Ordinal).Count()),
            ("StageCheckStatus", Enum.GetValues<StageCheckStatus>()
                .Select(s => WorkflowStatusMapper.WireStatus(s)).Distinct(StringComparer.Ordinal).Count()),
        };

        foreach (var (label, expected) in enumValues)
        {
            var mapped = mappedCounts.Single(c => c.Item1 == label).Item2;
            Assert.True(mapped == expected,
                $"{label}: expected one distinct wire value per enum value ({expected}), but got {mapped}.");
        }
    }

    [Fact]
    public void WireStatus_AwaitingApprovalIsTheOnlyMultiWordWireValueAcrossEnums()
    {
        var withHyphen = new[]
        {
            WorkflowStatusMapper.WireStatus(WorkflowRunStatus.AwaitingApproval),
            WorkflowStatusMapper.WireStatus(StageRunStatus.AwaitingApproval),
        };
        foreach (var value in withHyphen)
        {
            Assert.Equal("awaiting-approval", value);
            Assert.Contains("-", value);
        }
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

    private static string ExpectedWireFor(string enumName) =>
        enumName switch
        {
            "AwaitingApproval" => "awaiting-approval",
            _ => char.ToLowerInvariant(enumName[0]) + enumName[1..],
        };

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