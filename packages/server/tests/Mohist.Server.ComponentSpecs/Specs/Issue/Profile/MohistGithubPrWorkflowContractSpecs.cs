using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.ComponentSpecs.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;
using static Mohist.Server.ComponentSpecs.Specs.Issue.Profile.MohistGithubPrWorkflowDefinitionTestSupport;

namespace Mohist.Server.ComponentSpecs.Specs.Issue.Profile;

public class MohistGithubPrWorkflowContractSpecs
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

    [Fact]
    public void GithubPrRecoveryPrompts_DoNotPerformExplicitPushSideEffects()
    {
        var fixPrChecks = ReadBuiltinPrompt("fix-pr-checks.prompt");
        var resolveRebaseConflicts = ReadBuiltinPrompt("resolve-rebase-conflicts.prompt");

        Assert.DoesNotContain("force-with-lease", fixPrChecks, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```bash\ngit push", fixPrChecks, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recover:push", fixPrChecks, StringComparison.Ordinal);

        Assert.DoesNotContain("force-with-lease", resolveRebaseConflicts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("```bash\ngit push", resolveRebaseConflicts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recover:push", resolveRebaseConflicts, StringComparison.Ordinal);
    }

    [Fact]
    public void GithubPrWorkflowYaml_ParsesViaWorkflowYamlSerializer()
    {
        var yaml = ReadResourceYaml("mohist-github-pr.workflow.yaml");

        var definition = WorkflowYamlSerializer.FromYaml(yaml, "mohist/github-pr");

        Assert.Equal("mohist/github-pr", definition.Id);
        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());

        var planIds = definition.Stages[0].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(
            new[] { "workspace-prepare", "proposal", "specs", "design", "tasks", "self-review", "open-draft-pr" },
            planIds);
        Assert.Contains("mohist/create-github-pr", definition.Stages[0].Tasks.Select(t => t.Uses).ToArray());

        var checkTasks = JsonSerializer.Serialize(definition.Stages[2].Tasks);
        Assert.Contains("ai-review", checkTasks);
        Assert.Contains("push", checkTasks);
        Assert.Contains("mark-pr-ready", checkTasks);
        Assert.Contains("mohist/mark-github-pr-ready", checkTasks);
        Assert.Contains("mohist/push", checkTasks);
        Assert.DoesNotContain("create-pull-request", checkTasks);
        Assert.DoesNotContain("mohist/create-pull-request", checkTasks);

        var checkChecks = JsonSerializer.Serialize(definition.Stages[2].Checks);
        Assert.Contains("github-pr-status", checkChecks);
        Assert.DoesNotContain("merge-ready", checkChecks);

        var integrateIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "archive-change", "push", "merge-pr" }, integrateIds);
        Assert.Contains("mohist/merge-github-pr", definition.Stages[3].Tasks.Select(t => t.Uses).ToArray());

        var integrateChecks = JsonSerializer.Serialize(definition.Stages[3].Checks);
        Assert.Contains("merge-verified", integrateChecks);
        Assert.Contains("merged", integrateChecks);

        var serializedAll = JsonSerializer.Serialize(definition);
        Assert.Contains("prompts.fix-plan-review", serializedAll);
        Assert.Contains("prompts.fix-pr-checks", serializedAll);
    }

    [Fact]
    public void GithubPrWorkflowYaml_RoundTripsViaWorkflowYamlSerializer()
    {
        var yaml = ReadResourceYaml("mohist-github-pr.workflow.yaml");

        var definition = WorkflowYamlSerializer.FromYaml(yaml, "mohist/github-pr");
        var emitted = WorkflowYamlSerializer.ToYaml(definition);

        Assert.Contains("mohist/create-github-pr", emitted);
        Assert.Contains("mohist/github-pr-status", emitted);
        Assert.Contains("mohist/merge-github-pr", emitted);
        Assert.Contains("open-draft-pr", emitted);
        Assert.Contains("merge-verified", emitted);
        Assert.Contains("github-pr-status", emitted);
        Assert.Contains("when: errorCode=conflict", emitted);
        Assert.Contains("when: errorCode=script-failed", emitted);
        Assert.Contains("retrySelf: true", emitted);

        var reparsed = WorkflowYamlSerializer.FromYaml(emitted, "mohist/github-pr");
        Assert.Equal(definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Equal(
            definition.Stages.SelectMany(s => s.Tasks).Select(t => t.Id),
            reparsed.Stages.SelectMany(s => s.Tasks).Select(t => t.Id));
    }
}
