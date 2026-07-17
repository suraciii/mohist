using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

public class WorkflowProjectionTests
{
    [Fact]
    public void WorkflowStatusMapper_ProjectsTaskRequiredFiles()
    {
        var run = CreateRunWithTaskRequiredFiles();

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var task = view!.Stages[0].Tasks[0];
        Assert.NotNull(task.RequiredFiles);
        Assert.Single(task.RequiredFiles);
        Assert.Equal("proposal.md", task.RequiredFiles[0].Path);
        Assert.Equal("task-expect", task.RequiredFiles[0].Source);
        Assert.True(task.RequiredFiles[0].CanFetchContent);
    }

    [Fact]
    public void WorkflowStatusMapper_ProjectsTaskClassification()
    {
        var run = CreateRunWithTaskRequiredFiles();

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var userTask = view!.Stages[0].Tasks[0];
        var agentTask = view.Stages[0].Tasks[1];
        Assert.Equal(TaskClassification.UserFacing, userTask.Classification);
        Assert.Equal(TaskClassification.UserFacing, agentTask.Classification);
    }

    [Fact]
    public void WorkflowStatusMapper_ProjectsMultipleRequiredFiles()
    {
        var run = new WorkflowRun
        {
            Id = "wf-multiple",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "plan",
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = "proposal.1",
                            DefinitionId = "proposal",
                            Attempt = 1,
                            Title = "Generate proposal",
                            Status = TaskRunStatus.Completed,
                            Uses = "mohist/acp-agent",
                            WithInput = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                {"session": "plan"}
                                """),
                            ExpectInput = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                {"files": [{"path": "proposal.md"}, {"path": "design.md"}, {"path": "tasks.json"}]}
                                """),
                            RequiredFiles = TaskRunExtensions.ExtractRequiredFiles(
                                JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                    {"files": [{"path": "proposal.md"}, {"path": "design.md"}, {"path": "tasks.json"}]}
                                    """)),
                            Classification = TaskClassification.UserFacing
                        }
                    ]
                }
            ]
        };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var task = view!.Stages[0].Tasks[0];
        Assert.NotNull(task.RequiredFiles);
        Assert.Equal(3, task.RequiredFiles.Count);
        Assert.Equal("proposal.md", task.RequiredFiles[0].Path);
        Assert.Equal("design.md", task.RequiredFiles[1].Path);
        Assert.Equal("tasks.json", task.RequiredFiles[2].Path);
    }

    [Fact]
    public void WorkflowStatusMapper_WithoutRequiredFiles_ReturnsNullRequiredFiles()
    {
        var run = new WorkflowRun
        {
            Id = "wf-nofiles",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = WorkflowRunStatus.Running,
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
                            Title = "Build",
                            Status = TaskRunStatus.Running,
                            Uses = "core/script",
                            RequiredFiles = null,
                            Classification = TaskClassification.Orchestration
                        }
                    ]
                }
            ]
        };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var task = view!.Stages[0].Tasks[0];
        Assert.Null(task.RequiredFiles);
        Assert.Equal(TaskClassification.Orchestration, task.Classification);
    }

    [Fact]
    public void WorkflowStatusMapper_MapTasks_PreservesRequiredFiles()
    {
        var stage = new StageRun
        {
            Id = "plan",
            Attempt = 1,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks =
            [
                new TaskRun
                {
                    Id = "proposal.1",
                    DefinitionId = "proposal",
                    Attempt = 1,
                    Title = "Generate proposal",
                    Status = TaskRunStatus.Completed,
                    Uses = "mohist/acp-agent",
                    RequiredFiles =
                    [
                        new WorkflowTaskRequiredFile("proposal.md", "task-expect", true, null)
                    ],
                    Classification = TaskClassification.UserFacing
                }
            ]
        };

        var result = WorkflowStatusMapper.MapTasks(stage, definition: null);

        var task = Assert.Single(result);
        Assert.NotNull(task.RequiredFiles);
        Assert.Single(task.RequiredFiles);
        Assert.Equal("proposal.md", task.RequiredFiles[0].Path);
    }

    [Fact]
    public void WorkflowStatusMapper_MapTasks_FromDefinitionWithoutRuntimeTasks()
    {
        var stage = new StageRun
        {
            Id = "plan",
            Attempt = 1,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = []
        };

        var result = WorkflowStatusMapper.MapTasks(stage, definition: null);

        Assert.Empty(result);
    }

    [Fact]
    public void TaskRunExtensions_ExtractRequiredFiles_PreservesMarkers()
    {
        var expect = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
            {"files": [{"path": "design.md", "markers": ["<promise>PASS</promise>", "<promise>REVIEW</promise>"]}]}
            """);

        var result = TaskRunExtensions.ExtractRequiredFiles(expect);

        Assert.Single(result);
        var markers = result[0].Markers;
        Assert.NotNull(markers);
        Assert.Equal(2, markers.Length);
        Assert.Contains("<promise>PASS</promise>", markers);
        Assert.Contains("<promise>REVIEW</promise>", markers);
    }

    [Fact]
    public void ComputeStageProgress_ExcludesOrchestrationTasks()
    {
        var status = CreateStatusViewWithMixedTasks();

        var progress = ComputeStageProgress(status);

        Assert.NotNull(progress);
        Assert.Equal(1, progress.Total);
        Assert.Equal(0, progress.Completed);
        Assert.Equal(1, progress.Running);
        Assert.Equal(0, progress.Failed);
    }

    [Fact]
    public void ComputeStageProgress_DoesNotCountFailedAsCompleted()
    {
        var status = new WorkflowStatusView(
            WorkflowRunId: "wf-fail",
            Status: "running",
            CurrentStage: "build",
            Stages:
            [
                new StageStatusView(
                    Stage: "build",
                    Status: "running",
                    Order: 0,
                    Tasks:
                    [
                        new TaskStatusView("t1", "Failed task", "mohist/acp-agent", "failed", null, TaskClassification.UserFacing),
                        new TaskStatusView("t2", "Pending task", "mohist/acp-agent", "pending", null, TaskClassification.UserFacing)
                    ],
                    Checks: [],
                    ApprovalStatus: null,
                    Failure: null)
            ],
            PendingWork: null,
            Failure: null,
            AvailableActions: []);

        var progress = ComputeStageProgress(status);

        Assert.NotNull(progress);
        Assert.Equal(2, progress.Total);
        Assert.Equal(0, progress.Completed);
        Assert.Equal(1, progress.Failed);
    }

    [Fact]
    public void ComputeStageProgress_ReturnsNullForTerminalStatus()
    {
        var status = new WorkflowStatusView(
            WorkflowRunId: "wf-done",
            Status: "completed",
            CurrentStage: "done",
            Stages: [],
            PendingWork: null,
            Failure: null,
            AvailableActions: []);

        var progress = ComputeStageProgress(status);

        Assert.Null(progress);
    }

    [Fact]
    public void ComputeStageProgress_ReturnsNullWhenNoUserFacingTasks()
    {
        var status = new WorkflowStatusView(
            WorkflowRunId: "wf-orch",
            Status: "running",
            CurrentStage: "build",
            Stages:
            [
                new StageStatusView(
                    Stage: "build",
                    Status: "running",
                    Order: 0,
                    Tasks:
                    [
                        new TaskStatusView("orch1", "Internal", "core/script", "running", null, TaskClassification.Orchestration)
                    ],
                    Checks: [],
                    ApprovalStatus: null,
                    Failure: null)
            ],
            PendingWork: null,
            Failure: null,
            AvailableActions: []);

        var progress = ComputeStageProgress(status);

        Assert.Null(progress);
    }

    [Fact]
    public void ComputeStageProgress_ReturnsNullForApprovalOnlyWaitingStage()
    {
        var status = new WorkflowStatusView(
            WorkflowRunId: "wf-approval",
            Status: "running",
            CurrentStage: "check",
            Stages:
            [
                new StageStatusView(
                    Stage: "check",
                    Status: "awaiting-approval",
                    Order: 0,
                    Tasks:
                    [
                        new TaskStatusView("review.1", "Prepare review", "mohist/acp-agent", "completed", null, TaskClassification.UserFacing)
                    ],
                    Checks:
                    [
                        new CheckStatusView("merge-ready", "Merge ready", "mohist/merge-ready", "completed", null)
                    ],
                    ApprovalStatus: new ApprovalStatusView(null, "2024-01-01T01:00:00Z", null),
                    Failure: null)
            ],
            PendingWork: null,
            Failure: null,
            AvailableActions: []);

        var progress = ComputeStageProgress(status);

        Assert.Null(progress);
    }

    [Fact]
    public void WorkflowTaskRequiredFile_NoFileContentStored()
    {
        var requiredFile = new WorkflowTaskRequiredFile("proposal.md", "task-expect", true, null);

        var json = JsonSerializer.Serialize(requiredFile);

        Assert.DoesNotContain("content", json);
        Assert.DoesNotContain("proposal", json.ToLowerInvariant().Replace("proposal.md", ""));
    }

    [Fact]
    public void FakeFileContent_ReturnsNullForMissingFile()
    {
        var fake = new FakeFileContentService();
        fake.FileContents[("main", "exists.md")] = "file content";

        var result1 = fake.GetFileContent("main", "exists.md");
        var result2 = fake.GetFileContent("main", "missing.md");

        Assert.Equal("file content", result1);
        Assert.Null(result2);
    }

    private static WorkflowRun CreateRunWithTaskRequiredFiles()
    {
        return new WorkflowRun
        {
            Id = "wf-1",
            Metadata = new WorkflowRunMetadata("test", TestTime.UtcNow),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "plan",
            Stages =
            [
                new StageRun
                {
                    Id = "plan",
                    Attempt = 1,
                    RequiresApproval = false,
                    Status = StageRunStatus.Running,
                    Tasks =
                    [
                        new TaskRun
                        {
                            Id = "proposal.1",
                            DefinitionId = "proposal",
                            Attempt = 1,
                            Title = "Generate proposal",
                            Status = TaskRunStatus.Completed,
                            Uses = "mohist/acp-agent",
                            WithInput = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                {"session": "plan"}
                                """),
                            ExpectInput = JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                {"files": [{"path": "proposal.md"}]}
                                """),
                            RequiredFiles = TaskRunExtensions.ExtractRequiredFiles(
                                JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>("""
                                    {"files": [{"path": "proposal.md"}]}
                                    """)),
                            Classification = TaskClassification.UserFacing
                        },
                        new TaskRun
                        {
                            Id = "sync.1",
                            DefinitionId = "sync",
                            Attempt = 1,
                            Title = "Sync spec",
                            Status = TaskRunStatus.Running,
                            Uses = "mohist/acp-agent",
                            RequiredFiles = null,
                            Classification = TaskClassification.UserFacing
                        }
                    ]
                }
            ]
        };
    }

    private static WorkflowStatusView CreateStatusViewWithMixedTasks()
    {
        return new WorkflowStatusView(
            WorkflowRunId: "wf-mixed",
            Status: "running",
            CurrentStage: "build",
            Stages:
            [
                new StageStatusView(
                    Stage: "build",
                    Status: "running",
                    Order: 0,
                    Tasks:
                    [
                        new TaskStatusView("user.1", "User task", "mohist/acp-agent", "running", null, TaskClassification.UserFacing),
                        new TaskStatusView("orch.1", "Internal orch", "core/script", "running", null, TaskClassification.Orchestration)
                    ],
                    Checks: [],
                    ApprovalStatus: null,
                    Failure: null)
            ],
            PendingWork: null,
            Failure: null,
            AvailableActions: []);
    }

    private static WorkflowStageProgress? ComputeStageProgress(WorkflowStatusView status)
    {
        if (IsNonMeaningfulProgressState(status))
            return null;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null) return null;

        var userTasks = currentStage.Tasks.Where(t => t.Classification == TaskClassification.UserFacing).ToList();
        if (userTasks.Count == 0) return null;

        var total = userTasks.Count;
        var completed = userTasks.Count(t => t.Status == "completed");
        var running = userTasks.Count(t => t.Status == "running");
        var failed = userTasks.Count(t => t.Status == "failed");

        if (total == 0) return null;

        var currentTaskTitle = userTasks.FirstOrDefault(t => t.Status is "running" or "pending")?.Title;

        return new WorkflowStageProgress(
            status.CurrentStage!,
            total,
            completed,
            running,
            failed,
            currentTaskTitle);
    }

    private static bool IsNonMeaningfulProgressState(WorkflowStatusView status)
    {
        if (status.Status is "completed" or "failed" or "awaiting-approval" or "paused")
            return true;

        var currentStage = status.Stages.FirstOrDefault(s => s.Stage == status.CurrentStage);
        if (currentStage is null)
            return true;

        if (currentStage.Status == "awaiting-approval")
            return true;

        return currentStage.ApprovalStatus is { Result: null }
            || (currentStage.Tasks.All(t => t.Status == "completed") && currentStage.Checks.All(c => c.Status == "completed"));
    }

    [Fact]
    public void TaskStatusView_WithArtifactSummaries_CarriesSummaries()
    {
        var summaries = new List<ArtifactSummaryView>
        {
            new("art_abc", "review.md", "file", "review.md", TestTime.UtcNow, 1024),
            new("art_def", "design.md", "file", "design.md", TestTime.UtcNow, 2048),
        };

        var task = new TaskStatusView(
            "ai-review.1", "AI review", "mohist/acp-agent", "completed",
            null, TaskClassification.UserFacing, SessionName: null, ArtifactSummaries: summaries);

        Assert.NotNull(task.ArtifactSummaries);
        Assert.Equal(2, task.ArtifactSummaries.Count);
        Assert.Equal("art_abc", task.ArtifactSummaries[0].ArtifactId);
        Assert.Equal("review.md", task.ArtifactSummaries[0].Path);
        Assert.Equal("art_def", task.ArtifactSummaries[1].ArtifactId);
        Assert.Equal("design.md", task.ArtifactSummaries[1].Path);
    }

    [Fact]
    public void TaskStatusView_WithoutArtifactSummaries_DefaultsNull()
    {
        var task = new TaskStatusView("t1", "Basic task", "core/script", "running");

        Assert.Null(task.ArtifactSummaries);
    }

    [Fact]
    public void WorkflowStatusMapper_MapTasks_DoesNotSetArtifactSummaries()
    {
        var stage = new StageRun
        {
            Id = "build",
            Attempt = 1,
            RequiresApproval = false,
            Status = StageRunStatus.Completed,
            Tasks =
            [
                new TaskRun
                {
                    Id = "build.1",
                    DefinitionId = "build",
                    Attempt = 1,
                    Title = "Build step",
                    Status = TaskRunStatus.Completed,
                    Uses = "core/script",
                    RequiredFiles = null,
                    Classification = TaskClassification.Orchestration
                }
            ]
        };

        var result = WorkflowStatusMapper.MapTasks(stage, definition: null);

        var task = Assert.Single(result);
        Assert.Equal("build.1", task.Id);
        Assert.Equal("completed", task.Status);
        Assert.Null(task.ArtifactSummaries);
    }

    [Fact]
    public void ArtifactSummaryView_ContainsOnlyDtoFields_NoStoragePaths()
    {
        var summary = new ArtifactSummaryView(
            "art_123", "review.md", "file", "Review Report", TestTime.UtcNow, 4096);

        var json = JsonSerializer.Serialize(summary);

        Assert.Contains("ArtifactId", json);
        Assert.Contains("review.md", json);
        Assert.DoesNotContain("StoragePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentHash", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowStatusMapper_MapTasks_ProjectedSumariesAreNullByDefault()
    {
        var stage = new StageRun
        {
            Id = "plan",
            Attempt = 1,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Tasks = []
        };

        var result = WorkflowStatusMapper.MapTasks(stage, definition: null);

        Assert.Empty(result);
    }
}

public class FakeFileContentService
{
    public Dictionary<(string Branch, string FilePath), string?> FileContents { get; } = [];

    public string? GetFileContent(string branch, string filePath)
        => FileContents.GetValueOrDefault((branch, filePath));
}
