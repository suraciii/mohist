using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public sealed class VerificationLaneRecoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecoveryAttempts_LinkToOneFailedLaneAndRemainIdempotent()
    {
        var recovery = new RecoveryDefinition(
            2,
            [new RecoveryHandlerDefinition(null, [], RetrySelf: true)]);
        var definition = BuildDefinition(recovery);
        var run = CreateRun(definition);
        var stage = run.CurrentStage();
        var failed = stage.Tasks[1];
        failed.Status = TaskRunStatus.Failed;
        failed.WorkId = failed.Id;
        failed.WorkerId = "runner-1";
        failed.Lane = failed.Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "budget exhausted"),
            Detail = "command exceeded budget",
            FinishedAt = Now,
            WorkId = failed.WorkId,
        };

        var followUps = new[]
        {
            (new TaskDefinition("recover:fix-ci", "Fix CI", "core/script"), (int?)null),
            (new TaskDefinition(
                VerificationLaneCatalog.VerifyDotnet,
                "Verify dotnet",
                "core/script",
                With: new Dictionary<string, JsonElement?>
                {
                    ["timeout"] = JsonSerializer.SerializeToElement(1),
                },
                Recovery: recovery), (int?)1),
        };

        var firstEvents = run.AddRuntimeTaskAttempts(followUps, Now, failed.Id);
        Assert.Single(firstEvents);

        var helper = Assert.Single(stage.Tasks, task => task.DefinitionId == "recover:fix-ci");
        Assert.Null(helper.Lane);
        var retry = Assert.Single(stage.Tasks, task => task.DefinitionId == VerificationLaneCatalog.VerifyDotnet && task.Id != failed.Id);
        Assert.Equal(TaskRunStatus.Pending, retry.Status);
        Assert.Equal(failed.Id, helper.CausedByFailedTaskId);
        Assert.Equal(failed.Id, retry.CausedByFailedTaskId);
        Assert.Equal(failed.Lane.ConfiguredBudgetMs, retry.Lane!.ConfiguredBudgetMs);
        Assert.Equal(failed.Lane.Order, retry.Lane.Order);
        Assert.Equal(failed.Lane.LaneId, retry.Lane.LaneId);
        Assert.Equal(recovery, retry.Recovery);

        var duplicateEvents = run.AddRuntimeTaskAttempts(followUps, Now, failed.Id);
        Assert.Empty(duplicateEvents);
        Assert.Single(stage.Tasks, task => task.DefinitionId == "recover:fix-ci");
        Assert.Single(stage.Tasks, task => task.DefinitionId == VerificationLaneCatalog.VerifyDotnet && task.Id != failed.Id);
    }

    [Fact]
    public void FirstNonPassingLane_UsesFailedEvidenceWhileRetryIsPending()
    {
        var definition = BuildDefinition();
        var run = CreateRun(definition);
        var stage = run.CurrentStage();
        stage.Tasks[0].Lane = stage.Tasks[0].Lane! with { Outcome = VerificationLaneOutcome.Pass };
        stage.Tasks[1].Lane = stage.Tasks[1].Lane! with
        {
            Outcome = VerificationLaneOutcome.Timeout,
            Error = new ExecutionError("timeout", "budget exhausted"),
            Detail = "original timeout",
        };

        var retry = LaneTask(VerificationLaneCatalog.VerifyDotnet, 2, VerificationLaneOutcome.Pending);
        stage.Tasks.Insert(2, retry);

        var authoritative = VerificationLaneGate.AuthoritativeLaneAttempts(run);
        var lane = authoritative[VerificationLaneCatalog.VerifyDotnet];
        Assert.Equal(VerificationLaneOutcome.Timeout, lane.Outcome);
        Assert.Equal("original timeout", lane.Detail);
        Assert.Equal(retry.Id, lane.TaskRunId);
        Assert.Equal(1, VerificationLaneGate.FirstNonPassingLaneIndex(run));
        Assert.False(VerificationLaneGate.CanAdvanceBuildStage(run));
    }

    private static WorkflowRun CreateRun(WorkflowDefinition definition)
    {
        var run = WorkflowRun.Create("run-1", definition, Now);
        run.Start(Now);
        run.InitializeStage(definition.Stages[0].Tasks, [], Now, advance: false);
        run.BoundWorkflowDefinitionJson = WorkflowYamlSerializer.ToJson(definition);
        return run;
    }

    private static WorkflowDefinition BuildDefinition(RecoveryDefinition? recovery = null) =>
        new(new[]
        {
            new StageDefinition(
                "build",
                VerificationLaneCatalog.LaneIds.Select(id => new TaskDefinition(
                    id,
                    id,
                    "core/script",
                    new Dictionary<string, JsonElement?>
                    {
                        ["timeout"] = JsonSerializer.SerializeToElement(120000),
                    },
                    Recovery: recovery)).ToList(),
                []),
        });

    private static TaskRun LaneTask(string id, int attempt, VerificationLaneOutcome outcome) =>
        new()
        {
            Id = $"{id}.{attempt}",
            DefinitionId = id,
            Attempt = attempt,
            Title = id,
            Uses = "core/script",
            WithInput = new Dictionary<string, JsonElement?>
            {
                ["timeout"] = JsonSerializer.SerializeToElement(120000),
            },
            Status = TaskRunStatus.Pending,
            Lane = new VerificationLaneAttempt(
                id,
                VerificationLaneCatalog.OrderOf(id),
                120000,
                outcome,
                $"{id}.{attempt}"),
        };
}
