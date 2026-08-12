using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Domain.Run;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Domain;

public sealed class WorkflowRecoveryRoundTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static RecoveryDefinition Recovery => new(
        2,
        [new RecoveryHandlerDefinition(
            "error.code=conflict",
            [new TaskDefinition("fix", "Fix", "test/fix")],
            RetrySelf: true)]);

    private static WorkflowRun BuildRun()
    {
        var task = new TaskDefinition("review", "Review", "test/review", Recovery: Recovery);
        var run = WorkflowRun.Create("wf-recovery", new WorkflowDefinition(
            [new StageDefinition("check", [task], [])]), Now);
        run.Start(Now);
        run.InitializeStage([task], [], Now);
        run.AssignTo("runner-1", Now);
        return run;
    }

    [Fact]
    public void FreshTaskCarriesExplicitNullAndDefinitionProjectionExcludesAttemptState()
    {
        var run = BuildRun();
        var task = Assert.Single(run.CurrentStage().Tasks);

        Assert.Null(task.RecoveryRemaining);
        var definition = task.ToDefinition();
        Assert.Equal(task.DefinitionId, definition.Id);
        Assert.Equal(task.Title, definition.Title);
        Assert.Equal(task.Uses, definition.Uses);
        Assert.Equal(task.WithInput, definition.With);
        Assert.Equal(task.Artifacts, definition.Artifacts);
        Assert.Equal(task.SetVars, definition.SetVars);
        Assert.Equal(task.Recovery!.Budget, definition.Recovery!.Budget);
        Assert.Equal(task.Recovery.Handlers[0].When, definition.Recovery.Handlers[0].When);

        using var json = JsonDocument.Parse(JSON.Serialize(run));
        var persistedTask = json.RootElement.GetProperty("stages")[0].GetProperty("tasks")[0];
        Assert.True(persistedTask.TryGetProperty("recoveryRemaining", out var remaining));
        Assert.Equal(JsonValueKind.Null, remaining.ValueKind);
    }

    [Fact]
    public void ManualRetryCreatesFreshStateAndLeavesPreviousAttemptUnchanged()
    {
        var run = BuildRun();
        var stage = run.CurrentStage();
        var first = new TaskRun
        {
            Id = "review.1",
            DefinitionId = "review",
            Attempt = 1,
            Title = "Review",
            Uses = "test/review",
            Status = TaskRunStatus.Failed,
            Recovery = Recovery,
            RecoveryRemaining = 0,
        };
        stage.Tasks = [first];
        stage.Failure = new FailureDetails(FailureReason.TaskFailed, stage.Id, first.Id, Message: "round exhausted");
        stage.Status = StageRunStatus.Failed;
        run.Failure = stage.Failure;
        run.Status = WorkflowRunStatus.Failed;

        run.Retry(Now);

        Assert.Equal(TaskRunStatus.Failed, first.Status);
        Assert.Equal(0, first.RecoveryRemaining);
        var retried = Assert.Single(run.CurrentStage().Tasks.Skip(1));
        Assert.Equal(2, retried.Recovery!.Budget);
        Assert.Null(retried.RecoveryRemaining);
        Assert.Equal("review.1", first.Id);
    }

    [Fact]
    public void ContinuationStateIsBoundToItsDeclaration()
    {
        var run = BuildRun();
        var definition = Assert.Single(run.CurrentStage().Tasks).ToDefinition();

        var continuation = TaskRun.MakeContinuationTask(
            run.CurrentStage().Tasks,
            definition,
            run.CurrentStage().Attempt,
            1,
            run.Stages.SelectMany(stage => stage.Tasks));

        Assert.Equal(1, continuation.RecoveryRemaining);
        Assert.Equal(2, continuation.Recovery!.Budget);
        Assert.Throws<InvalidOperationException>(() =>
            TaskRun.MakeContinuationTask(
                run.CurrentStage().Tasks,
                new TaskDefinition("orphan", "Orphan", "test/orphan"),
                run.CurrentStage().Attempt,
                1,
                run.Stages.SelectMany(stage => stage.Tasks)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    public void ContinuationStateOutsideDeclaredBudgetIsPreserved(int recoveryRemaining)
    {
        var run = BuildRun();
        var definition = Assert.Single(run.CurrentStage().Tasks).ToDefinition();

        var continuation = TaskRun.MakeContinuationTask(
            run.CurrentStage().Tasks,
            definition,
            run.CurrentStage().Attempt,
            recoveryRemaining,
            run.Stages.SelectMany(stage => stage.Tasks));

        Assert.Equal(recoveryRemaining, continuation.RecoveryRemaining);
        Assert.Equal(2, continuation.Recovery!.Budget);
    }

    [Fact]
    public void LegacyRecoveryChainIsNormalizedIdempotently()
    {
        const string legacy = """
            {
              "stages": [{
                "tasks": [
                  {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error.code=conflict","tasks":[],"retrySelf":true}]}},
                  {"definitionId":"review","attempt":2,"recovery":{"budget":1,"handlers":[{"when":"error.code=conflict","tasks":[],"retrySelf":true}]}},
                  {"definitionId":"review","attempt":3,"recovery":{"budget":0,"handlers":[{"when":"error.code=conflict","tasks":[],"retrySelf":true}]}}
                ]
              }]
            }
            """;

        var normalized = WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(legacy);
        using var json = JsonDocument.Parse(normalized);
        var tasks = json.RootElement.GetProperty("stages")[0].GetProperty("tasks");
        Assert.Equal(new[] { 2, 1, 0 }, tasks.EnumerateArray()
            .Select(t => t.GetProperty("recoveryRemaining").GetInt32()).ToArray());
        Assert.All(tasks.EnumerateArray(), task =>
            Assert.Equal(2, task.GetProperty("recovery").GetProperty("budget").GetInt32()));
        Assert.Equal(normalized, WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(normalized));
    }

    [Fact]
    public void LegacyWorkflowProfileAnnotationIsRestoredToCanonicalRunBinding()
    {
        const string legacy = """
            {"metadata":{"annotations":{"workflowProfileId":"legacy-profile"}}}
            """;

        var normalized = WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(legacy);

        using var json = JsonDocument.Parse(normalized);
        Assert.Equal("legacy-profile", json.RootElement.GetProperty("workflowProfileId").GetString());
        Assert.Equal("legacy-profile", json.RootElement.GetProperty("metadata")
            .GetProperty("annotations").GetProperty("workflowProfileId").GetString());
        Assert.Equal(normalized, WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(normalized));
    }

    [Fact]
    public void ExplicitNullAndZeroRecoveryStateAreNotMigrated()
    {
        const string json = """
            {"stages":[{"tasks":[
              {"definitionId":"review","recovery":{"budget":2,"handlers":[]},"recoveryRemaining":null},
              {"definitionId":"review","recovery":{"budget":2,"handlers":[]},"recoveryRemaining":0}
            ]}]}
            """;

        Assert.Equal(json, WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(json));
    }

    [Fact]
    public void JsonRoundTripPreservesExplicitNullAndNumericRecoveryState()
    {
        var run = BuildRun();
        var stage = run.CurrentStage();
        stage.Tasks =
        [
            stage.Tasks[0],
            new TaskRun
            {
                Id = "review.2",
                DefinitionId = "review",
                Attempt = 2,
                Title = "Review",
                Uses = "test/review",
                Status = TaskRunStatus.Pending,
                Recovery = Recovery,
                RecoveryRemaining = 1,
            }
        ];

        var restored = JSON.Deserialize<WorkflowRun>(JSON.Serialize(run));

        Assert.NotNull(restored);
        Assert.Equal(new int?[] { null, 1 }, restored!.CurrentStage().Tasks.Select(t => t.RecoveryRemaining).ToArray());
    }

    [Fact]
    public void NestedHandlerTaskRecoveryBudgetDifferenceIsRejected()
    {
        // The outer attempts encode their consumed allowance as the root
        // recovery.budget (2 -> 1), which normalization must absorb. But a
        // handler task's own recovery declaration is definition data: a budget
        // difference there (4 -> 1) is ambiguous and must be rejected rather
        // than silently rewritten to the canonical outer attempt's value.
        const string json = """
            {"stages":[{"tasks":[
              {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"budget":2},"recovery":{"budget":4,"handlers":[]}}],"retrySelf":true}]}},
              {"definitionId":"review","attempt":2,"recovery":{"budget":1,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"budget":2},"recovery":{"budget":1,"handlers":[]}}],"retrySelf":true}]}}
            ]}]}
            """;

        Assert.Throws<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(json));
    }

    [Fact]
    public void NestedHandlerTaskRecoveryDeclarationIsNormalizedWhenIdentical()
    {
        // Root recovery.budget encodes consumption (2 -> 1) and is absorbed;
        // the handler task's own recovery declaration is identical across
        // attempts, so normalization succeeds and the nested declaration is
        // preserved verbatim (budget 4 on both attempts).
        const string legacy = """
            {"stages":[{"tasks":[
              {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"budget":2},"recovery":{"budget":4,"handlers":[]}}],"retrySelf":true}]}},
              {"definitionId":"review","attempt":2,"recovery":{"budget":1,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"budget":2},"recovery":{"budget":4,"handlers":[]}}],"retrySelf":true}]}}
            ]}]}
            """;

        var normalized = WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(legacy);
        using var json = JsonDocument.Parse(normalized);
        var tasks = json.RootElement.GetProperty("stages")[0].GetProperty("tasks");
        Assert.Equal(new[] { 2, 1 }, tasks.EnumerateArray()
            .Select(t => t.GetProperty("recoveryRemaining").GetInt32()).ToArray());
        Assert.All(tasks.EnumerateArray(), task =>
        {
            Assert.Equal(2, task.GetProperty("recovery").GetProperty("budget").GetInt32());
            Assert.Equal(4, task.GetProperty("recovery").GetProperty("handlers")[0]
                .GetProperty("tasks")[0].GetProperty("recovery").GetProperty("budget").GetInt32());
        });
    }

    [Fact]
    public void LegacyRecoveryDeclarationsDifferingInWithRecoveryBudgetAreRejected()
    {
        const string json = """
            {"stages":[{"tasks":[
              {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"recovery":{"budget":2}}}],"retrySelf":true}]}},
              {"definitionId":"review","attempt":2,"recovery":{"budget":1,"handlers":[{"when":"error=one","tasks":[{"id":"fix","title":"Fix","with":{"recovery":{"budget":1}}}],"retrySelf":true}]}}
            ]}]}
            """;

        Assert.Throws<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(json));
    }

    [Fact]
    public void AmbiguousLegacyRecoveryDeclarationsAreRejected()
    {
        const string json = """
            {"stages":[{"tasks":[
              {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error.code=one","tasks":[],"retrySelf":true}]}},
              {"definitionId":"review","attempt":2,"recovery":{"budget":1,"handlers":[{"when":"error.code=two","tasks":[],"retrySelf":true}]}}
            ]}]}
            """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(json));
        Assert.Contains("definition id 'review'", error.Message);
    }

    [Fact]
    public void LegacyGroupWithPartialRecoveryDeclarationSkipsNormalizationInsteadOfFailing()
    {
        // Historical persistence can leave a definition id's attempts split:
        // some carry a recovery declaration, others do not (older writers,
        // non-recovery task shapes, partially migrated rows). That ambiguity
        // is not data corruption and must not poison the whole run projection.
        // The group is skipped (attempts preserved as-is) rather than thrown.
        const string json = """
            {"stages":[{"tasks":[
              {"definitionId":"review","attempt":1,"recovery":{"budget":2,"handlers":[{"when":"error.code=conflict","tasks":[],"retrySelf":true}]}},
              {"definitionId":"review","attempt":2},
              {"definitionId":"review","attempt":3,"recovery":{"budget":0,"handlers":[{"when":"error.code=conflict","tasks":[],"retrySelf":true}]}}
            ]}]}
            """;

        var normalized = WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(json);

        using var result = JsonDocument.Parse(normalized);
        var tasks = result.RootElement.GetProperty("stages")[0].GetProperty("tasks");
        var attempts = tasks.EnumerateArray().Select(t => t.GetProperty("attempt").GetInt32()).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, attempts);
        Assert.All(tasks.EnumerateArray(), task => Assert.False(task.TryGetProperty("recoveryRemaining", out _)));
        Assert.Equal(normalized, WorkflowRunStateDataUpgrader.MigrateLegacyWorkflowRunJson(normalized));
    }

    [Fact]
    public void WorkDispatchResponseSerializesExplicitNullRecoveryState()
    {
        var response = new Mohist.Server.Api.WorkDispatchResponse(
            "wf-recovery", "review.1", "test/review", null, null, "task", "check", "Review",
            Recovery: JSON.Serialize(Recovery), RecoveryRemaining: null);

        using var json = JsonDocument.Parse(JSON.Serialize(response));
        Assert.True(json.RootElement.TryGetProperty("recoveryRemaining", out var remaining));
        Assert.Equal(JsonValueKind.Null, remaining.ValueKind);
    }
}
