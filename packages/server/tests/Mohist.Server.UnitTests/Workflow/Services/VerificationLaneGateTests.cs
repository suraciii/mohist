using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public sealed class VerificationLaneGateTests
{
    private const int BudgetMs = 60000;

    [Fact]
    public void IsLaneEnabledRun_FalseForRunWithoutSnapshot()
    {
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(BuildDefinition(stageTasks: BuildSixLaneTasks())),
        };

        Assert.False(VerificationLaneGate.IsLaneEnabledRun(run));
    }

    [Fact]
    public void IsLaneEnabledRun_FalseForLegacyAggregateSnapshot()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[]
            {
                new TaskDefinition(
                    Id: "verify",
                    Title: "Verify",
                    Uses: "core/script"),
            }, Array.Empty<CheckDefinition>()),
        });

        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
            BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition),
        };

        Assert.False(VerificationLaneGate.IsLaneEnabledRun(run));
    }

    [Fact]
    public void IsLaneEnabledRun_TrueForSixLaneSnapshot()
    {
        var definition = BuildDefinition(stageTasks: BuildSixLaneTasks());
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
            BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition),
        };

        Assert.True(VerificationLaneGate.IsLaneEnabledRun(run));
    }

    [Fact]
    public void FirstNonPassingLaneIndex_NoSnapshot_ReturnsMinusOne()
    {
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(BuildDefinition(stageTasks: BuildSixLaneTasks())),
        };

        Assert.Equal(-1, VerificationLaneGate.FirstNonPassingLaneIndex(run));
    }

    [Fact]
    public void FirstNonPassingLaneIndex_AllPending_ReturnsZero()
    {
        var run = CreateSixLaneRun(outcome: VerificationLaneOutcome.Pending);
        Assert.Equal(0, VerificationLaneGate.FirstNonPassingLaneIndex(run));
    }

    [Fact]
    public void FirstNonPassingLaneIndex_FirstTwoPassed_ReturnsSecondIndex()
    {
        var run = CreateSixLaneRun(
            (VerificationLaneCatalog.VerifyInstall, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyDotnet, VerificationLaneOutcome.Pass));

        Assert.Equal(2, VerificationLaneGate.FirstNonPassingLaneIndex(run));
    }

    [Fact]
    public void FirstNonPassingLaneIndex_AllPassed_ReturnsMinusOne()
    {
        var run = CreateSixLaneRun(
            Enumerable.Range(0, VerificationLaneCatalog.LaneIds.Count)
                .Select(i => (VerificationLaneCatalog.LaneIds[i], VerificationLaneOutcome.Pass))
                .ToArray());

        Assert.Equal(-1, VerificationLaneGate.FirstNonPassingLaneIndex(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_LegacyAggregate_AlwaysTrue()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", new[]
            {
                new TaskDefinition(Id: "verify", Title: "Verify", Uses: "core/script"),
            }, Array.Empty<CheckDefinition>()),
        });
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
        };

        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_FailsWhenAnyLaneMissing()
    {
        var run = CreateSixLaneRunWithoutLaneAttempts();
        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_FailsWhenAnyLaneFailed()
    {
        var run = CreateSixLaneRun(
            (VerificationLaneCatalog.VerifyInstall, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyDotnet, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyWebTypecheck, VerificationLaneOutcome.Fail));
        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_FailsWhenAnyLaneTimedOut()
    {
        var run = CreateSixLaneRun(
            (VerificationLaneCatalog.VerifyInstall, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyDotnet, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyWebTypecheck, VerificationLaneOutcome.Pass),
            (VerificationLaneCatalog.VerifyWebTests, VerificationLaneOutcome.Timeout));
        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void CanAdvanceBuildStage_TrueWhenAllPass()
    {
        var run = CreateSixLaneRun(
            Enumerable.Range(0, VerificationLaneCatalog.LaneIds.Count)
                .Select(i => (VerificationLaneCatalog.LaneIds[i], VerificationLaneOutcome.Pass))
                .ToArray());
        Assert.True(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    [Fact]
    public void IsClaimableLaneTask_LegacyRun_AllowsAllTasks()
    {
        var definition = BuildDefinition(stageTasks: new[]
        {
            new TaskDefinition(Id: "verify", Title: "Verify", Uses: "core/script"),
        });
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
        };
        var task = run.Stages[0].Tasks[0];

        Assert.True(VerificationLaneGate.IsClaimableLaneTask(run, task));
    }

    [Fact]
    public void IsClaimableLaneTask_LaterLaneBlockedUntilPredecessorPasses()
    {
        var run = CreateSixLaneRun(outcome: VerificationLaneOutcome.Pending);
        var stage = run.Stages[0];

        // Only the first non-passing lane is claimable.
        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var task = stage.Tasks[i];
            var expected = i == 0;
            Assert.Equal(expected, VerificationLaneGate.IsClaimableLaneTask(run, task));
        }
    }

    [Fact]
    public void IsClaimableLaneTask_AfterFirstLanePasses_OnlySecondLaneClaimable()
    {
        var run = CreateSixLaneRun(
            (VerificationLaneCatalog.VerifyInstall, VerificationLaneOutcome.Pass));
        var stage = run.Stages[0];

        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var task = stage.Tasks[i];
            var expected = i == 1;
            Assert.Equal(expected, VerificationLaneGate.IsClaimableLaneTask(run, task));
        }
    }

    private static IReadOnlyList<TaskDefinition> BuildSixLaneTasks() =>
        VerificationLaneCatalog.LaneIds.Select(id =>
            new TaskDefinition(
                Id: id,
                Title: id,
                Uses: "core/script",
                With: new Dictionary<string, JsonElement?>
                {
                    ["timeout"] = JsonDocument.Parse(JsonSerializer.Serialize(BudgetMs)).RootElement.Clone(),
                })).ToList();

    private static WorkflowDefinition BuildDefinition(IReadOnlyList<TaskDefinition> stageTasks) =>
        new(new[]
        {
            new StageDefinition("build", stageTasks, Array.Empty<CheckDefinition>()),
        });

    private static List<StageRun> BuildStages(WorkflowDefinition definition)
    {
        var stages = new List<StageRun>();
        foreach (var stage in definition.Stages)
        {
            var runStage = new StageRun
            {
                Id = stage.Stage,
                Attempt = 1,
                RequiresApproval = stage.RequiresApproval,
                Status = StageRunStatus.Running,
            };
            foreach (var task in stage.Tasks)
            {
                runStage.Tasks.Add(new TaskRun
                {
                    Id = task.Id + ".1",
                    DefinitionId = task.Id,
                    Attempt = 1,
                    Title = task.Title ?? task.Id,
                    Status = TaskRunStatus.Pending,
                    Uses = task.Uses,
                    WithInput = task.With,
                    Classification = TaskClassification.Orchestration,
                });
            }
            stages.Add(runStage);
        }
        return stages;
    }

    private static WorkflowRun CreateSixLaneRun(params (string LaneId, VerificationLaneOutcome Outcome)[] outcomes)
    {
        var allOutcomes = VerificationLaneCatalog.LaneIds
            .Select(id => outcomes.FirstOrDefault(o => o.LaneId == id))
            .Select(o => o == default ? VerificationLaneOutcome.Pending : o.Outcome)
            .ToArray();
        return CreateSixLaneRunFromOutcomes(allOutcomes);
    }

    private static WorkflowRun CreateSixLaneRun(VerificationLaneOutcome outcome)
    {
        var allOutcomes = Enumerable.Repeat(outcome, VerificationLaneCatalog.LaneIds.Count).ToArray();
        return CreateSixLaneRunFromOutcomes(allOutcomes);
    }

    private static WorkflowRun CreateSixLaneRunFromOutcomes(VerificationLaneOutcome[] outcomes)
    {
        var definition = BuildDefinition(stageTasks: BuildSixLaneTasks());
        var run = new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
            BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition),
        };

        for (var i = 0; i < VerificationLaneCatalog.LaneIds.Count; i++)
        {
            var task = run.Stages[0].Tasks[i];
            task.Lane = new VerificationLaneAttempt(
                LaneId: VerificationLaneCatalog.LaneIds[i],
                Order: i,
                ConfiguredBudgetMs: BudgetMs,
                Outcome: outcomes[i],
                TaskRunId: task.Id);
        }

        return run;
    }

    private static WorkflowRun CreateSixLaneRunWithoutLaneAttempts()
    {
        var definition = BuildDefinition(stageTasks: BuildSixLaneTasks());
        return new WorkflowRun
        {
            Id = "run-1",
            Metadata = new WorkflowRunMetadata("test", DateTimeOffset.UnixEpoch),
            Status = WorkflowRunStatus.Running,
            CurrentStageId = "build",
            Stages = BuildStages(definition),
            BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition),
        };
    }
}