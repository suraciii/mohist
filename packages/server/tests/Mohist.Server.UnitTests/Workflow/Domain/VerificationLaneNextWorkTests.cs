using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

/// <summary>
/// Verifies the ordered-dispatch gate: NextWork exposes verification lanes
/// only in catalog order, does not expose a later lane before its predecessor
/// passes, and the build stage cannot advance while any required lane is
/// pending, missing, failed, or timed out. Legacy aggregate runs keep their
/// existing dispatch and gate behavior.
/// </summary>
public sealed class VerificationLaneNextWorkTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NextWork_LegacyRun_ReturnsFirstPendingTask_NoLaneGate()
    {
        var run = CreateRun(aggregateOnly: true);
        var work = run.NextWork();

        Assert.NotNull(work);
        var task = Assert.IsType<WorkflowTaskWork>(work);
        Assert.Equal("verify.1", task.Id);
    }

    [Fact]
    public void NextWork_LaneEnabledRun_ReturnsFirstPendingLaneOnly()
    {
        var run = CreateLaneEnabledRun();

        var first = run.NextWork();
        Assert.NotNull(first);
        var task = Assert.IsType<WorkflowTaskWork>(first);
        Assert.Equal(VerificationLaneCatalog.VerifyInstall + ".1", task.Id);
    }

    [Fact]
    public void NextWork_LaneEnabledRun_BlocksLaterLaneWhileFirstPending()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];

        // Mark lane 1 (verify-install) as completed-pass so lane 2 becomes claimable.
        stage.Tasks[0].Status = TaskRunStatus.Completed;
        stage.Tasks[0].Lane = stage.Tasks[0].Lane! with { Outcome = VerificationLaneOutcome.Pass };

        Assert.True(VerificationLaneGate.IsLaneEnabledRun(run));
        Assert.Equal(1, VerificationLaneGate.FirstNonPassingLaneIndex(run));

        // Lane 2 (verify-dotnet) is the only claimable lane while its
        // predecessor has passed but it has not. Lane 3 (verify-web-typecheck)
        // MUST remain blocked.
        var blocked = run.NextWork();
        Assert.NotNull(blocked);
        var blockedTask = Assert.IsType<WorkflowTaskWork>(blocked);
        Assert.Equal(VerificationLaneCatalog.VerifyDotnet + ".1", blockedTask.Id);
        Assert.DoesNotContain(VerificationLaneCatalog.VerifyWebTypecheck, blockedTask.Id);
        Assert.DoesNotContain(VerificationLaneCatalog.VerifyRunnerTypecheck, blockedTask.Id);
    }

    [Fact]
    public void NextWork_LaneEnabledRun_OnlyFirstNonPassingLaneIsClaimable()
    {
        var run = CreateLaneEnabledRun();
        // Complete all but the last lane.
        var stage = run.Stages[0];
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count - 1; i++)
        {
            stage.Tasks[i].Status = TaskRunStatus.Completed;
            stage.Tasks[i].Lane = stage.Tasks[i].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }

        var next = run.NextWork();
        Assert.NotNull(next);
        var nextTask = Assert.IsType<WorkflowTaskWork>(next);
        Assert.Equal(VerificationLaneCatalog.LaneIds[^1] + ".1", nextTask.Id);
    }

    [Fact]
    public void NextWork_LaneEnabledRun_BlocksDownstreamTaskWhenLaneResultIsMissing()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];
        foreach (var task in stage.Tasks)
        {
            task.Status = TaskRunStatus.Completed;
            task.Lane = task.Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }
        stage.Tasks[0].Lane = null;
        AddPendingDownstreamTask(stage);

        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
        Assert.Null(run.NextWork());
        Assert.Null(run.CurrentPendingWork());
    }

    [Fact]
    public void NextWork_LaneEnabledRun_BlocksDownstreamTaskWhenLaneIsNotPassing()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];
        foreach (var task in stage.Tasks)
        {
            task.Status = TaskRunStatus.Completed;
            task.Lane = task.Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }
        stage.Tasks[2].Status = TaskRunStatus.Failed;
        stage.Tasks[2].Lane = stage.Tasks[2].Lane! with
        {
            Outcome = VerificationLaneOutcome.Fail,
            Error = new ExecutionError("script-failed", "lane failed"),
        };
        AddPendingDownstreamTask(stage);

        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
        Assert.Null(run.NextWork());
        Assert.Null(run.CurrentPendingWork());
    }

    [Fact]
    public void NextWork_LaneRecoveryBarrier_DoesNotFallThroughToChecks()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];
        stage.Tasks[0].Status = TaskRunStatus.Failed;
        stage.Tasks[0].Lane = stage.Tasks[0].Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "lane timed out"),
        };
        stage.Checks.Add(new StageCheck
        {
            Name = "build-check",
            Title = "Build check",
            Status = StageCheckStatus.Pending,
        });

        Assert.Null(run.NextWork());
        Assert.Null(run.CurrentPendingWork());
    }

    [Fact]
    public void CanAdvanceBuildStage_LegacyRun_AlwaysTrue()
    {
        var run = CreateRun(aggregateOnly: true);
        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_LaneEnabledRun_FailsWhenAnyLaneMissing()
    {
        var run = CreateLaneEnabledRun();
        // No lane attempts set; the gate must not synthesize pending lanes.
        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_LaneEnabledRun_TrueWhenAllPass()
    {
        var run = CreateLaneEnabledRun();
        var stage = run.Stages[0];
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            stage.Tasks[i].Lane = stage.Tasks[i].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        }
        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    private static WorkflowRun CreateRun(bool aggregateOnly)
    {
        var definition = aggregateOnly
            ? new WorkflowDefinition(new[]
            {
                new StageDefinition("build", new[]
                {
                    new TaskDefinition("verify", "Verify", "core/script"),
                }, Array.Empty<CheckDefinition>()),
            })
            : new WorkflowDefinition(new[]
            {
                new StageDefinition("build",
                    VerificationLaneCatalog.LaneIds
                        .Select(id => new TaskDefinition(id, id, "core/script", new Dictionary<string, JsonElement?>
                        {
                            ["timeout"] = JsonDocument.Parse("120000").RootElement.Clone(),
                        }))
                        .ToList(),
                    Array.Empty<CheckDefinition>()),
            });

        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("Issue 42", CreatedAt, ProjectId: "project-1", IssueNumber: 42),
            Status = WorkflowRunStatus.Ready,
            CurrentStageId = "build",
            Stages = new List<StageRun>(),
            BoundWorkflowDefinitionJson = aggregateOnly ? null : WorkflowYamlSerializer.ToJson(definition),
        };

        var stageRun = new StageRun
        {
            Id = "build",
            Attempt = 1,
            RequiresApproval = false,
            Status = StageRunStatus.Running,
            Initialized = true,
        };
        foreach (var task in definition.Stages[0].Tasks)
        {
            var taskRun = new TaskRun
            {
                Id = task.Id + ".1",
                DefinitionId = task.Id,
                Attempt = 1,
                Title = task.Title ?? task.Id,
                Status = TaskRunStatus.Pending,
                Uses = task.Uses,
                WithInput = task.With,
                Classification = TaskClassification.Orchestration,
            };
            if (VerificationLaneCatalog.IsKnownLane(task.Id))
            {
                taskRun.Lane = new VerificationLaneAttempt(
                    LaneId: task.Id,
                    Order: VerificationLaneCatalog.OrderOf(task.Id),
                    ConfiguredBudgetMs: 120000,
                    Outcome: VerificationLaneOutcome.Pending,
                    TaskRunId: taskRun.Id);
            }
            stageRun.Tasks.Add(taskRun);
        }
        run.Stages.Add(stageRun);
        return run;
    }

    private static WorkflowRun CreateLaneEnabledRun() => CreateRun(aggregateOnly: false);

    private static void AddPendingDownstreamTask(StageRun stage)
    {
        stage.Tasks.Add(new TaskRun
        {
            Id = "push.1",
            DefinitionId = "push",
            Attempt = 1,
            Title = "Push",
            Uses = "mohist/push",
            Status = TaskRunStatus.Pending,
        });
    }
}