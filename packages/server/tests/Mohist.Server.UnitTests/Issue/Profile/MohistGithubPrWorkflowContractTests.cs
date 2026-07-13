using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using static Mohist.Server.UnitTests.Issue.Profile.MohistGithubPrWorkflowDefinitionTestSupport;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistGithubPrWorkflowContractTests
{
    [Fact]
    public void GithubPrWorkflowDefinition_MergePrRecovery_DeclaresBaseMovedAndPrChecksFailedHandlers()
    {
        var mergePr = MohistWorkflow.GithubPrWorkflowDefinition
            .Stages.Single(s => s.Stage == "integrate")
            .Tasks.Single(t => t.Id == "merge-pr");

        var recovery = mergePr.Recovery;
        Assert.NotNull(recovery);
        Assert.Equal(2, recovery!.Budget);
        var handlers = recovery.Handlers.ToList();
        Assert.Equal(3, handlers.Count);

        var baseMoved = handlers.Single(h => h.When == "errorCode=base-moved");
        Assert.True(baseMoved.RetrySelf);
        var baseMovedTasks = baseMoved.Tasks.ToList();
        Assert.Equal(new[] { "recover:rebase", "recover:push" }, baseMovedTasks.Select(t => t.Id).ToArray());

        var recoverRebase = baseMovedTasks.Single(t => t.Id == "recover:rebase");
        Assert.Equal("mohist/rebase", recoverRebase.Uses);
        var rebaseWith = recoverRebase.With!;
        Assert.Equal("${{ repository.baseBranch }}", rebaseWith["baseBranch"]!.Value.GetString());
        Assert.Equal("origin", rebaseWith["remote"]!.Value.GetString());
        Assert.False(rebaseWith["squash"]!.Value.GetBoolean());
        Assert.False(rebaseWith.ContainsKey("conflictMode"));

        var rebaseRecovery = recoverRebase.Recovery;
        Assert.NotNull(rebaseRecovery);
        Assert.Equal(2, rebaseRecovery!.Budget);
        var rebaseHandlers = rebaseRecovery.Handlers.ToList();
        Assert.Single(rebaseHandlers);
        var conflictHandler = rebaseHandlers.Single(h => h.When == "errorCode=conflict");
        Assert.False(conflictHandler.RetrySelf);
        var conflictTasks = conflictHandler.Tasks.ToList();
        Assert.Single(conflictTasks);
        var resolveConflicts = conflictTasks.Single(t => t.Id == "recover:resolve-rebase-conflicts");
        Assert.Equal("mohist/acp-agent", resolveConflicts.Uses);
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", resolveConflicts.With!["prompt"]!.Value.GetString());

        var recoverPushBaseMoved = baseMovedTasks.Single(t => t.Id == "recover:push");
        Assert.Equal("mohist/push", recoverPushBaseMoved.Uses);
        var pushWith = recoverPushBaseMoved.With!;
        Assert.Equal("${{ workspace.branch }}", pushWith["source"]!.Value.GetString());
        Assert.Equal("${{ workspace.branch }}", pushWith["target"]!.Value.GetString());
        Assert.True(pushWith["force"]!.Value.GetBoolean());
        Assert.False(pushWith.ContainsKey("forceWithLease"));

        var prChecksFailed = handlers.Single(h => h.When == "errorCode=pr-checks-failed");
        Assert.True(prChecksFailed.RetrySelf);
        var prChecksTasks = prChecksFailed.Tasks.ToList();
        Assert.Equal(new[] { "recover:fix-pr-checks", "recover:push" }, prChecksTasks.Select(t => t.Id).ToArray());

        var fixPrChecks = prChecksTasks.Single(t => t.Id == "recover:fix-pr-checks");
        Assert.Equal("mohist/acp-agent", fixPrChecks.Uses);
        Assert.Equal("${{ prompts.fix-pr-checks }}", fixPrChecks.With!["prompt"]!.Value.GetString());

        var recoverPushPrChecks = prChecksTasks.Single(t => t.Id == "recover:push");
        Assert.Equal("mohist/push", recoverPushPrChecks.Uses);
        Assert.Equal("${{ workspace.branch }}", recoverPushPrChecks.With!["source"]!.Value.GetString());
        Assert.True(recoverPushPrChecks.With!["forceWithLease"]!.Value.GetBoolean());

        var protectionConflict = handlers.Single(h => h.When == "errorCode=protection-conflict");
        Assert.True(protectionConflict.RetrySelf);
        Assert.Empty(protectionConflict.Tasks);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_TaskIdsHaveNoStagePrefixesAndRecoveryUsesRecover()
    {
        var definition = MohistWorkflow.GithubPrWorkflowDefinition;

        var allTaskIds = definition.Stages
            .SelectMany(s => s.Tasks.Select(t => t.Id))
            .Concat(definition.Stages.SelectMany(s => s.Tasks
                .Where(t => t.Recovery is not null)
                .SelectMany(t => ExtractRecoveryTaskIds(t.Recovery!))))
            .ToList();

        Assert.NotEmpty(allTaskIds);
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("plan:", StringComparison.Ordinal), $"Task id '{id}' has plan: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("check:", StringComparison.Ordinal), $"Task id '{id}' has check: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("integrate:", StringComparison.Ordinal), $"Task id '{id}' has integrate: prefix"));
        Assert.All(allTaskIds, id => Assert.False(id.StartsWith("build:", StringComparison.Ordinal), $"Task id '{id}' has build: prefix"));

        var recoveryIds = allTaskIds.Where(id => id.StartsWith("recover:", StringComparison.Ordinal)).ToList();
        Assert.Contains("recover:fix-plan-review", recoveryIds);
        Assert.Contains("recover:fix-tests", recoveryIds);
        Assert.Contains("recover:fix-review-findings", recoveryIds);
        Assert.Contains("recover:rebase", recoveryIds);
        Assert.Contains("recover:push", recoveryIds);
        Assert.Contains("recover:fix-pr-checks", recoveryIds);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_AllTaskPromptsAreNamedReferences()
    {
        var definition = MohistWorkflow.GithubPrWorkflowDefinition;

        var inlinePromptTasks = new List<(string Stage, string Task, string Prompt)>();
        foreach (var stage in definition.Stages)
        {
            foreach (var task in CollectAllTasks(stage))
            {
                if (task.With is null) continue;
                if (!task.With.TryGetValue("prompt", out var promptElement) || promptElement is null) continue;
                var promptStr = promptElement.Value.ValueKind == JsonValueKind.String
                    ? promptElement.Value.GetString()
                    : JsonSerializer.Serialize(promptElement.Value);
                if (string.IsNullOrEmpty(promptStr)) continue;
                if (!promptStr.TrimStart().StartsWith("${{", StringComparison.Ordinal))
                {
                    inlinePromptTasks.Add((stage.Stage, task.Id, promptStr!));
                }
            }

            foreach (var recoveryTask in EnumerateRecoveryTaskElements(stage))
            {
                if (!recoveryTask.TryGetProperty("with", out var withEl)) continue;
                if (!withEl.TryGetProperty("prompt", out var promptEl)) continue;
                var promptStr = promptEl.ValueKind == JsonValueKind.String
                    ? promptEl.GetString()
                    : promptEl.GetRawText();
                if (string.IsNullOrEmpty(promptStr)) continue;
                if (!promptStr!.TrimStart().StartsWith("${{", StringComparison.Ordinal))
                {
                    inlinePromptTasks.Add((stage.Stage, recoveryTask.GetProperty("id").GetString() ?? "", promptStr));
                }
            }
        }

        Assert.Empty(inlinePromptTasks);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_DoesNotReferenceRemovedActions()
    {
        var definition = MohistWorkflow.GithubPrWorkflowDefinition;
        var integrateStage = definition.Stages.Single(s => s.Stage == "integrate");

        var usedActionIds = CollectAllTasks(integrateStage)
            .Select(t => t.Uses)
            .Where(u => !string.IsNullOrEmpty(u))
            .Concat(EnumerateRecoveryTaskElements(integrateStage)
                .Select(t => t.TryGetProperty("uses", out var usesEl) && usesEl.ValueKind == JsonValueKind.String ? usesEl.GetString() : null)
                .Where(u => !string.IsNullOrEmpty(u)))
            .ToList();

        Assert.DoesNotContain(usedActionIds, u => u == "mohist/create-pull-request");
        Assert.DoesNotContain(usedActionIds, u => u == "mohist/merge-pull-request");
        Assert.DoesNotContain(usedActionIds, u => u == "mohist/publish-via-pr");
    }

}
