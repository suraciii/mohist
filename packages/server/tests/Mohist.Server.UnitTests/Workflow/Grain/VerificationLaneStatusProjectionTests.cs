using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Grain;

/// <summary>
/// Tests the build-stage verification-lane status projection.
/// Lane-enabled runs project all six lanes, including pending or missing
/// states, and preserve pass/fail/timeout evidence with diagnostics.
/// Legacy runs without lane fields remain readable and do not project a
/// lane summary.
/// </summary>
public sealed class VerificationLaneStatusProjectionTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildStatusView_LegacyRun_VerificationLanesIsNull()
    {
        var run = CreateRun(BuildDefinition(
            new[] { new TaskDefinition("verify", "Verify", "core/script") }),
            definitionJson: null);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Null(view!.VerificationLanes);
    }

    [Fact]
    public void BuildStatusView_LaneEnabledRun_PendingLanesAreProjected()
    {
        var run = CreateLaneEnabledRun();

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view!.VerificationLanes);
        Assert.Equal(VerificationLaneCatalog.LaneIds.Count, view.VerificationLanes!.Lanes.Count);
        foreach (var lane in view.VerificationLanes.Lanes)
        {
            Assert.Equal("pending", lane.Outcome);
        }
        Assert.Equal(VerificationLaneCatalog.VerifyInstall, view.VerificationLanes.FirstNonPassingLane);
        Assert.False(view.VerificationLanes.AllPassing);
    }

    [Fact]
    public void BuildStatusView_LaneEnabledRun_MissingLanesKeepConfiguredBudgets()
    {
        var definition = BuildDefinition(
            VerificationLaneCatalog.LaneIds
                .Select(id => new TaskDefinition(
                    id,
                    id,
                    "core/script",
                    new Dictionary<string, JsonElement?>
                    {
                        ["timeout"] = JsonDocument.Parse("120000").RootElement.Clone(),
                    }))
                .ToList());
        var run = CreateRun(definition, WorkflowYamlSerializer.ToJson(definition));

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view!.VerificationLanes);
        Assert.All(view.VerificationLanes!.Lanes, lane =>
        {
            Assert.Equal("pending", lane.Outcome);
            Assert.Equal(120000, lane.ConfiguredBudgetMs);
        });
    }

    [Fact]
    public void BuildStatusView_LaneEnabledRun_AllPassing()
    {
        var run = CreateLaneEnabledRun();
        foreach (var stage in run.Stages)
        foreach (var task in stage.Tasks)
        {
            task.Status = TaskRunStatus.Completed;
            if (task.Lane is not null)
                task.Lane = task.Lane with { Outcome = VerificationLaneOutcome.Pass };
        }

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view!.VerificationLanes);
        Assert.True(view.VerificationLanes!.AllPassing);
        Assert.Null(view.VerificationLanes.FirstNonPassingLane);
        Assert.All(view.VerificationLanes.Lanes, lane => Assert.Equal("pass", lane.Outcome));
    }

    [Fact]
    public void BuildStatusView_LaneEnabledRun_FailAndTimeoutPreserveDiagnostics()
    {
        var run = CreateLaneEnabledRun();
        var buildStage = run.Stages[0];

        // First lane passes
        buildStage.Tasks[0].Status = TaskRunStatus.Completed;
        buildStage.Tasks[0].Lane = buildStage.Tasks[0].Lane! with
        {
            Outcome = VerificationLaneOutcome.Pass,
        };

        // Second lane fails
        buildStage.Tasks[1].Status = TaskRunStatus.Failed;
        buildStage.Tasks[1].Lane = buildStage.Tasks[1].Lane! with
        {
            Outcome = VerificationLaneOutcome.Fail,
            Error = new ExecutionError("script-failed", "dotnet test returned 1"),
            Detail = "src/foo.test.ts(12,1): error TS2304",
            FinishedAt = CreatedAt.AddSeconds(30),
        };

        // Third lane times out
        buildStage.Tasks[2].Status = TaskRunStatus.Failed;
        buildStage.Tasks[2].Lane = buildStage.Tasks[2].Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "killed after 120000 ms"),
            Detail = "Command exceeded its 120000 ms budget",
            FinishedAt = CreatedAt.AddMinutes(2),
        };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.NotNull(view!.VerificationLanes);
        var lanes = view.VerificationLanes!.Lanes;
        Assert.Equal("pass", lanes[0].Outcome);
        Assert.Equal("fail", lanes[1].Outcome);
        Assert.Equal("script-failed", lanes[1].Error!.Code);
        Assert.Equal("dotnet test returned 1", lanes[1].Error!.Message);
        Assert.Equal("src/foo.test.ts(12,1): error TS2304", lanes[1].Detail);
        Assert.Equal("timeout", lanes[2].Outcome);
        Assert.Equal("timeout", lanes[2].Error!.Code);
        Assert.Equal("killed after 120000 ms", lanes[2].Error!.Message);
        Assert.Equal(VerificationLaneCatalog.VerifyDotnet, view.VerificationLanes.FirstNonPassingLane);
    }

    [Fact]
    public void BuildStatusView_PendingRetryKeepsOriginalFailureAndUsesRetryIdentity()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];
        stage.Tasks[0].Lane = stage.Tasks[0].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        stage.Tasks[1].Lane = stage.Tasks[1].Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "original timeout"),
            Detail = "command exceeded its budget",
        };

        var retry = new TaskRun
        {
            Id = $"{VerificationLaneCatalog.VerifyDotnet}.2",
            DefinitionId = VerificationLaneCatalog.VerifyDotnet,
            Attempt = 2,
            Title = VerificationLaneCatalog.VerifyDotnet,
            Uses = "core/script",
            Status = TaskRunStatus.Pending,
            Lane = new VerificationLaneAttempt(
                VerificationLaneCatalog.VerifyDotnet,
                1,
                120000,
                VerificationLaneOutcome.Pending,
                $"{VerificationLaneCatalog.VerifyDotnet}.2"),
        };
        stage.Tasks.Insert(2, retry);

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        var lane = Assert.Single(
            view!.VerificationLanes!.Lanes,
            candidate => candidate.LaneId == VerificationLaneCatalog.VerifyDotnet);
        Assert.Equal("timeout", lane.Outcome);
        Assert.Equal("original timeout", lane.Error!.Message);
        Assert.Equal("command exceeded its budget", lane.Detail);
        Assert.Equal($"{VerificationLaneCatalog.VerifyDotnet}.2", lane.TaskRunId);
        Assert.Equal(VerificationLaneCatalog.VerifyDotnet, view.VerificationLanes.FirstNonPassingLane);
    }

    [Fact]
    public void MapTasks_LaneTask_ExposesLaneView()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];

        var taskViews = WorkflowStatusMapper.MapTasks(stage, definition: null);

        Assert.All(taskViews, view => Assert.NotNull(view.Lane));
        Assert.Equal(VerificationLaneCatalog.VerifyInstall, taskViews[0].Lane!.LaneId);
        Assert.Equal(0, taskViews[0].Lane!.Order);
    }

    [Fact]
    public void MapTasks_NonLaneTask_LaneViewIsNull()
    {
        var run = CreateRun(BuildDefinition(new[]
        {
            new TaskDefinition("compile", "Compile", "core/script"),
        }), definitionJson: null);
        var stage = run.Stages[0];

        var taskViews = WorkflowStatusMapper.MapTasks(stage, definition: null);

        Assert.All(taskViews, view => Assert.Null(view.Lane));
    }

    [Fact]
    public void BuildStatusView_LaneEnabledRun_PreservesFirstNonPassingIndex()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];

        stage.Tasks[0].Status = TaskRunStatus.Completed;
        stage.Tasks[0].Lane = stage.Tasks[0].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        stage.Tasks[1].Status = TaskRunStatus.Completed;
        stage.Tasks[1].Lane = stage.Tasks[1].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        stage.Tasks[2].Status = TaskRunStatus.Completed;
        stage.Tasks[2].Lane = stage.Tasks[2].Lane! with { Outcome = VerificationLaneOutcome.Pass };

        var view = WorkflowStatusMapper.BuildStatusView(run, definition: null);

        Assert.Equal(VerificationLaneCatalog.VerifyWebTests, view!.VerificationLanes!.FirstNonPassingLane);
    }

    [Fact]
    public void ReloadedRun_JsonRoundTrip_PreservesLaneOutcomesAndDiagnostics()
    {
        var run = CreateLaneEnabledRun();
        var buildStage = run.Stages[0];

        buildStage.Tasks[0].Status = TaskRunStatus.Completed;
        buildStage.Tasks[0].Lane = buildStage.Tasks[0].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        buildStage.Tasks[1].Status = TaskRunStatus.Failed;
        buildStage.Tasks[1].Lane = buildStage.Tasks[1].Lane! with
        {
            Outcome = VerificationLaneOutcome.Fail,
            Error = new ExecutionError("script-failed", "npm ci exited with 1"),
            Detail = "npm error code E401",
            WorkId = "verify-dotnet.1",
            FinishedAt = CreatedAt.AddSeconds(10),
        };
        buildStage.Tasks[2].Status = TaskRunStatus.Failed;
        buildStage.Tasks[2].Lane = buildStage.Tasks[2].Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "killed after 120000 ms"),
            Detail = "Command exceeded its 120000 ms budget",
            WorkId = "verify-web-typecheck.1",
            FinishedAt = CreatedAt.AddMinutes(2),
        };

        // The run state (including the bound definition snapshot and the
        // additive lane metadata) must survive the persisted JSON round-trip
        // and still project the same pass/fail/timeout evidence.
        var json = JSON.Serialize(run);
        var reloaded = JSON.Deserialize<WorkflowRun>(json)!;

        Assert.True(VerificationLaneGate.IsLaneEnabledRun(reloaded));
        var view = WorkflowStatusMapper.BuildStatusView(reloaded, definition: null);
        Assert.NotNull(view!.VerificationLanes);

        var lanes = view.VerificationLanes!.Lanes;
        Assert.Equal("pass", lanes[0].Outcome);
        Assert.Equal("fail", lanes[1].Outcome);
        Assert.Equal("script-failed", lanes[1].Error!.Code);
        Assert.Equal("npm error code E401", lanes[1].Detail);
        Assert.Equal("verify-dotnet.1", lanes[1].WorkId);
        Assert.Equal("timeout", lanes[2].Outcome);
        Assert.Equal("timeout", lanes[2].Error!.Code);
        Assert.Equal("Command exceeded its 120000 ms budget", lanes[2].Detail);
        Assert.Equal(VerificationLaneCatalog.VerifyDotnet, view.VerificationLanes!.FirstNonPassingLane);
        Assert.False(view.VerificationLanes!.AllPassing);
    }

    private static WorkflowRun CreateRun(WorkflowDefinition definition, string? definitionJson)
    {
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("Issue 42", CreatedAt, ProjectId: "project-1", IssueNumber: 42),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = new List<StageRun>(),
            BoundWorkflowDefinitionJson = definitionJson,
        };

        foreach (var stageDef in definition.Stages)
        {
            var stage = new StageRun
            {
                Id = stageDef.Stage,
                Attempt = 1,
                RequiresApproval = stageDef.RequiresApproval,
                Status = StageRunStatus.Running,
            };
            foreach (var taskDef in stageDef.Tasks)
            {
                var task = new TaskRun
                {
                    Id = taskDef.Id + ".1",
                    DefinitionId = taskDef.Id,
                    Attempt = 1,
                    Title = taskDef.Title ?? taskDef.Id,
                    Status = TaskRunStatus.Pending,
                    Uses = taskDef.Uses,
                    WithInput = taskDef.With,
                    Classification = TaskClassification.Orchestration,
                };
                if (VerificationLaneCatalog.IsKnownLane(taskDef.Id))
                {
                    task.Lane = new VerificationLaneAttempt(
                        LaneId: taskDef.Id,
                        Order: VerificationLaneCatalog.OrderOf(taskDef.Id),
                        ConfiguredBudgetMs: 120000,
                        Outcome: VerificationLaneOutcome.Pending,
                        TaskRunId: task.Id);
                }
                stage.Tasks.Add(task);
            }
            run.Stages.Add(stage);
        }
        return run;
    }

    private static WorkflowDefinition BuildDefinition(IReadOnlyList<TaskDefinition> buildTasks) =>
        new(new[]
        {
            new StageDefinition("build", buildTasks, Array.Empty<CheckDefinition>()),
        });

    private static WorkflowRun CreateLaneEnabledRun() =>
        CreateRun(BuildDefinition(
            VerificationLaneCatalog.LaneIds
                .Select(id => new TaskDefinition(id, id, "core/script", new Dictionary<string, JsonElement?>
                {
                    ["timeout"] = JsonDocument.Parse("120000").RootElement.Clone(),
                }))
                .ToList()),
            definitionJson: WorkflowYamlSerializer.ToJson(BuildDefinition(
                VerificationLaneCatalog.LaneIds
                    .Select(id => new TaskDefinition(id, id, "core/script", new Dictionary<string, JsonElement?>
                    {
                        ["timeout"] = JsonDocument.Parse("120000").RootElement.Clone(),
                    }))
                    .ToList())));
}