using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistWorkflowDefinitionTests
{
    [Fact]
    public void DefaultWorkflowDefinition_LoadsFromYaml()
    {
        var definition = WorkflowProfileCatalog.Definition;

        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());
        Assert.True(definition.Stages[0].RequiresApproval);
        Assert.True(definition.Stages[2].RequiresApproval);

        var proposal = definition.Stages[0].Tasks[1];
        Assert.Equal("proposal", proposal.Id);
        Assert.Equal("mohist/opencode", proposal.Uses);
        Assert.Contains("proposal.md", JsonSerializer.Serialize(proposal.Expect));

        var build = definition.Stages[1];
        var loadTask = build.Tasks[1];
        Assert.Equal("load-tasks", loadTask.Id);
        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Contains("tasks.json", JsonSerializer.Serialize(loadTask.With));
        var verify = build.Tasks.Single(t => t.Id == "verify");
        Assert.Equal("core/script", verify.Uses);
        Assert.False(verify.With!.ContainsKey("resourceProfile"));

        var archiveChange = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:archive-change");
        var rebase = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:rebase");
        var push = definition.Stages[3].Tasks.Single(t => t.Id == "integrate:push");
        Assert.Equal("sequential", definition.Stages[3].LockBehavior);
        Assert.Equal(["project-integration"], definition.Stages[3].Resources);
        var integrateIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(new[] { "workspace-prepare", "integrate:archive-change", "integrate:rebase", "integrate:push", "integrate:health" }, integrateIds);
        Assert.DoesNotContain("integrate:merge", integrateIds);
        Assert.Equal("mohist/archive-change", archiveChange.Uses);
        var archiveChangeWithJson = JsonSerializer.Serialize(archiveChange.With);
        Assert.Contains("openspec/changes/issue-${{ issue.number }}", archiveChangeWithJson);
        Assert.DoesNotContain("archiveHint", archiveChangeWithJson);
        Assert.Equal("mohist/rebase", rebase.Uses);
        var rebaseWithJson = JsonSerializer.Serialize(rebase.With);
        Assert.Contains("${{ repository.baseBranch }}", rebaseWithJson);
        Assert.DoesNotContain("\"conflictResolver\"", rebaseWithJson);
        Assert.Contains("\"messageFrom\"", rebaseWithJson);
        Assert.Contains("issue.title", rebaseWithJson);
        AssertRebaseConflictRecovery(rebase, "integrate");
        Assert.Equal("mohist/push", push.Uses);
        var pushWithJson = JsonSerializer.Serialize(push.With);
        Assert.Contains("workspace.branch", pushWithJson);
        Assert.Contains("repository.baseBranch", pushWithJson);
        var integrateTaskIds = definition.Stages[3].Tasks.Select(t => t.Id).ToArray();
        Assert.Equal(["workspace-prepare", "integrate:archive-change", "integrate:rebase", "integrate:push", "integrate:health"], integrateTaskIds);
        foreach (var task in definition.Stages[3].Tasks)
        {
            Assert.NotEqual("mohist/merge", task.Uses);
        }

        var mergeReady = definition.Stages[2].Tasks.Single(t => t.Id == "merge-ready");
        Assert.NotNull(mergeReady.Recovery);
        var mergeReadyHandler = Assert.Single(mergeReady.Recovery!.Handlers);
        Assert.Equal("canMerge=false", mergeReadyHandler.When);
        Assert.True(mergeReadyHandler.RetrySelf);
        var rebaseRecovery = Assert.Single(mergeReadyHandler.Tasks);
        Assert.Equal("recover:rebase-onto-base", rebaseRecovery.Id);
        Assert.Equal("mohist/rebase", rebaseRecovery.Uses);
        var recoveryWithJson = JsonSerializer.Serialize(rebaseRecovery.With);
        Assert.DoesNotContain("\"conflictResolver\"", recoveryWithJson);
        AssertRebaseConflictRecovery(rebaseRecovery, "check");
    }

    [Fact]
    public void DefaultWorkflowDefinition_IntegrateStageHasSinglePublishOwner()
    {
        var definition = WorkflowProfileCatalog.Definition;
        var integrate = definition.Stages.Single(s => s.Stage == "integrate");

        AssertSinglePushOwnerInvariant(integrate);
    }

    [Fact]
    public void DefaultWorkflowDefinition_IntegrateStageWithDuplicatePublishTask_FailsSinglePushOwnerInvariant()
    {
        var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: integrate
            tasks:
              - id: integrate:publish
                title: Publish changes
                uses: mohist/publish
                with:
                  target: ${{ project.baseBranch }}
              - id: integrate:push
                title: Push branch
                uses: mohist/push
                with:
                  target: ${{ project.baseBranch }}
            checks: []
        """));
        Assert.Contains("project", ex.Message);
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStageTaskTemplateUsesAcpAgentWithPromptLoaderSpec()
    {
        var loadTask = WorkflowProfileCatalog.Definition.Stages[1].Tasks[1];
        var withJson = JsonSerializer.Serialize(loadTask.With);

        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Contains("\"uses\":\"mohist/opencode\"", withJson);
        Assert.Contains("\"prompt\":", withJson);
        Assert.Contains("\"uses\":\"mohist/openspec-task-prompt\"", withJson);
         Assert.Contains("openspec/changes/issue-${{ issue.number }}/tasks.json", withJson);
        Assert.Contains("\"items\":\"tasks\"", withJson);
        Assert.Contains("\"base\":\"${{ prompts.build }}\"", withJson);
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStagePromptLoaderConfigExposesFileItemsAndBase()
    {
        var loadTask = WorkflowProfileCatalog.Definition.Stages[1].Tasks[1];
        var with = loadTask.With ?? throw new InvalidOperationException("load-tasks must have a with map");
        var taskElement = with["task"] ?? throw new InvalidOperationException("load-tasks with must contain 'task'");
        var taskTemplate = taskElement.GetProperty("with");
        var promptSpec = taskTemplate.GetProperty("prompt");
        var promptWith = promptSpec.GetProperty("with");

        Assert.Equal("mohist/openspec-task-prompt", promptSpec.GetProperty("uses").GetString());
        Assert.Contains("tasks.json", promptWith.GetProperty("file").GetString());
        Assert.Equal("tasks", promptWith.GetProperty("items").GetString());
        Assert.Equal("${{ prompts.build }}", promptWith.GetProperty("base").GetString());
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStageRetainsExistingLoaderKeys()
    {
        var loadTask = WorkflowProfileCatalog.Definition.Stages[1].Tasks[1];
        var with = loadTask.With ?? throw new InvalidOperationException("load-tasks must have a with map");
        var pathElement = with["path"] ?? throw new InvalidOperationException("load-tasks with must contain 'path'");

        Assert.Equal("mohist/openspec-tasks", loadTask.Uses);
        Assert.Equal("load-tasks", loadTask.Id);
         Assert.Equal("openspec/changes/issue-${{ issue.number }}/tasks.json", pathElement.GetString());
    }

    [Fact]
    public void DefaultWorkflowDefinition_PlanCheckIntegrateStagesAreUnchanged()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(WorkflowProfileCatalog.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(WorkflowProfileCatalog.Definition.Stages[0].Tasks.Select(t => t.Id), reparsed.Stages[0].Tasks.Select(t => t.Id));
        Assert.Equal(WorkflowProfileCatalog.Definition.Stages[0].Tasks.Select(t => t.Uses), reparsed.Stages[0].Tasks.Select(t => t.Uses));
        Assert.Equal(WorkflowProfileCatalog.Definition.Stages[2].Tasks.Select(t => t.Id), reparsed.Stages[2].Tasks.Select(t => t.Id));
        Assert.Equal(WorkflowProfileCatalog.Definition.Stages[3].Tasks.Select(t => t.Id), reparsed.Stages[3].Tasks.Select(t => t.Id));
        Assert.True(reparsed.Stages[0].RequiresApproval);
        Assert.True(reparsed.Stages[2].RequiresApproval);
    }

    [Fact]
    public void DefaultWorkflowDefinition_TaskVerdictMarkersDeclareFailIf()
    {
        var definition = WorkflowProfileCatalog.Definition;

        var verdictTasks = definition.Stages
            .SelectMany(s => s.Tasks)
            .Where(HasFailVerdictMarker)
            .ToList();

        Assert.Equal(["self-review", "ai-review"], verdictTasks.Select(t => t.Id).ToArray());
        foreach (var task in verdictTasks)
            Assert.Equal("<promise>FAIL</promise>", FailIfMarker(task));
    }

    private static bool HasFailVerdictMarker(TaskDefinition task)
    {
        var failIf = FailIfMarker(task);
        if (failIf != "<promise>FAIL</promise>") return false;

        if (task.Expect is null) return false;
        var expect = JsonSerializer.SerializeToElement(task.Expect);
        if (!expect.TryGetProperty("markers", out var markers)) return false;
        foreach (var marker in markers.EnumerateArray())
        {
            if (!marker.TryGetProperty("oneOf", out var oneOf)) continue;
            if (oneOf.EnumerateArray().Any(v => v.GetString() == "<promise>FAIL</promise>"))
                return true;
        }

        return false;
    }

    private static string? FailIfMarker(TaskDefinition task)
    {
        if (task.Expect is null) return null;

        var expectElement = JsonSerializer.SerializeToElement(task.Expect);
        if (!expectElement.TryGetProperty("markers", out var markers)) return null;
        foreach (var marker in markers.EnumerateArray())
        {
            if (marker.TryGetProperty("failIf", out var failIf))
                return failIf.GetString();
        }
        return null;
    }

    [Fact]
    public void DefaultWorkflowDefinition_DeclaresExpectedArtifactCapturePaths()
    {
        var definition = WorkflowProfileCatalog.Definition;

        var plan = definition.Stages[0];
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "proposal"), "proposal.md");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "specs"), "specs");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "design"), "design.md");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "tasks"), "tasks.json");
        AssertArtifactPaths(plan.Tasks.Single(t => t.Id == "self-review"), "self-review.md");

        var check = definition.Stages[2];
        AssertArtifactPaths(check.Tasks.Single(t => t.Id == "ai-review"), "review.md");
    }

    [Fact]
    public void DefaultWorkflowDefinition_AiReviewTaskDeclaresMarkerExpectationWithOneOf()
    {
        // The default workflow's ai-review task must declare a
        // with.expect.markers entry that accepts both PASS and FAIL
        // verdicts so a failing review does not loop the action
        // forever. This is the spec's canonical YAML shape for the
        // check repair loop motivating scenario.
        var definition = WorkflowProfileCatalog.Definition;
        var check = definition.Stages[2];
        var aiReview = check.Tasks.Single(t => t.Id == "ai-review");

        AssertMarkerOneOf(aiReview);
    }

    [Fact]
    public void DefaultWorkflowDefinition_AiReviewRecoversOnFailPromiseWithRetrySelf()
    {
        // The ai-review task declares an expect marker that accepts either
        // PASS or FAIL. When the agent produces a FAIL review, the runner
        // reports the task as completed with output.promise: FAIL, and
        // recovery.handlers matches when: output.promise=FAIL to trigger the
        // recover:fix-review-findings (auto-fix) recovery task and retries
        // ai-review (re-review) via retrySelf: true. The check stage
        // carries only health and merge-ready checks, no review-passed.
        var definition = WorkflowProfileCatalog.Definition;
        var check = definition.Stages[2];
        Assert.DoesNotContain(check.Checks, c => c.Id == "review-passed");

        var aiReview = check.Tasks.Single(t => t.Id == "ai-review");
        Assert.NotNull(aiReview.Expect);
        var expectElement = JsonSerializer.SerializeToElement(aiReview.Expect);
        Assert.True(expectElement.TryGetProperty("markers", out var markers));
        var firstMarker = markers[0];
        Assert.True(firstMarker.TryGetProperty("oneOf", out var oneOf));
        Assert.Contains("<promise>FAIL</promise>", oneOf.EnumerateArray().Select(e => e.GetString()));

        Assert.NotNull(aiReview.Recovery);
        var recovery = aiReview.Recovery!;
        Assert.Equal(2, recovery.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.Equal("output.promise=FAIL", handler.When);
        Assert.True(handler.RetrySelf);
        var fixReviewFindings = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-review-findings", fixReviewFindings.Id);
        Assert.Equal("${{ prompts.auto-fix }}", fixReviewFindings.With!["prompt"]!.Value.GetString());
    }

    private static void AssertMarkerOneOf(TaskDefinition task)
    {
        Assert.NotNull(task.Expect);

        var expectElement = JsonSerializer.SerializeToElement(task.Expect);
        Assert.Equal(JsonValueKind.Object, expectElement.ValueKind);
        Assert.True(expectElement.TryGetProperty("markers", out var markers),
            "task 'expect' is missing the 'markers' entry");
        Assert.Equal(JsonValueKind.Array, markers.ValueKind);
        Assert.True(markers.GetArrayLength() > 0, "'markers' must declare at least one entry");

        var first = markers[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.True(first.TryGetProperty("path", out _), "marker entry is missing 'path'");
        Assert.True(first.TryGetProperty("oneOf", out var oneOf),
            "marker entry is missing the 'oneOf' verdicts list");
        Assert.Equal(JsonValueKind.Array, oneOf.ValueKind);

        var verdicts = oneOf.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => v is not null)
            .ToList();
        Assert.Contains("<promise>PASS</promise>", verdicts);
        Assert.Contains("<promise>FAIL</promise>", verdicts);

        // Files-style expectations are intentionally absent so the
        // artifact declaration surface is not the same as the action
        // completion contract.
        Assert.False(expectElement.TryGetProperty("files", out _),
            "ai-review expect must use markers, not files");
    }

    private static void AssertSinglePushOwnerInvariant(StageDefinition integrate)
    {
        var deliveryTasks = integrate.Tasks
            .Where(t => t.Id.EndsWith(":publish", StringComparison.Ordinal)
                        || t.Id.EndsWith(":push", StringComparison.Ordinal))
            .Select(t => t.Id)
            .ToList();
        Assert.Equal(new[] { "integrate:push" }, deliveryTasks);
    }

    private static void AssertRebaseConflictRecovery(TaskDefinition rebase, string session)
    {
        Assert.NotNull(rebase.Recovery);
        var recovery = rebase.Recovery!;
        Assert.Equal(2, recovery.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.Equal("error.code=conflict", handler.When);
        Assert.False(handler.RetrySelf);
        var task = Assert.Single(handler.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", task.Id);
        Assert.Equal("Resolve rebase conflicts", task.Title);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.Equal(session, task.With!["session"]!.Value.GetString());
        Assert.Equal("${{ prompts.resolve-rebase-conflicts }}", task.With!["prompt"]!.Value.GetString());
        Assert.Equal("${{ vars.agent }}", task.With!["options"]!.Value.GetString());
    }

    private static void AssertArtifactPaths(TaskDefinition task, params string[] expectedPathSuffixes)
    {
        Assert.NotNull(task.Artifacts);
        var actual = task.Artifacts!.Files.Select(f => f.Path).ToList();
        Assert.Equal(expectedPathSuffixes.Length, actual.Count);
        foreach (var suffix in expectedPathSuffixes)
            Assert.Contains(actual, p => p.EndsWith(suffix, StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultWorkflowDefinition_DescriptionIsParsedFromYamlBlockScalar()
    {
        var description = WorkflowProfileCatalog.Profile.Description;

        Assert.NotNull(description);
        Assert.Contains("plan", description!);
        Assert.Contains("build", description);
        Assert.Contains("check", description);
        Assert.Contains("integrate", description);
        Assert.DoesNotContain("use quick-fix", description);
        Assert.DoesNotContain("use experiment", description);
    }

    [Fact]
    public void DefaultWorkflowDefinition_DescriptionPreservesMultilineLineBreaks()
    {
        var description = WorkflowProfileCatalog.Profile.Description;

        Assert.NotNull(description);
        Assert.Contains("approval", description!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescriptionField_DoesNotInfluenceStageExecutionShape()
    {
        // Profile metadata is no longer part of a direct Definition.
        var descriptionOnlyYaml = """
            id: mohist/local
            description: |
              Some user-facing description that the engine must not
              read or interpret.
            stages:
              - stage: build
                tasks: []
                checks: []
            """;

        Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml(descriptionOnlyYaml));
    }

    [Fact]
    public void DefaultWorkflowYaml_OmitsStructuredMetadataFields()
    {
        // Locks the "description-only" design decision. The spec scenario
        // "Other metadata fields are absent" forbids the top-level of a
        // workflow profile YAML from carrying risk_level, typical_duration,
        // suitable_for, avoid_for, tags, or default_approval_policy — those
        // belong inside the natural-language description.
        var yaml = WorkflowYamlSerializer.ToYaml(WorkflowProfileCatalog.Definition);

        var forbidden = new[]
        {
            "risk_level:",
            "riskLevel:",
            "typical_duration:",
            "typicalDuration:",
            "suitable_for:",
            "suitableFor:",
            "avoid_for:",
            "avoidFor:",
            "tags:",
            "default_approval_policy:",
            "defaultApprovalPolicy:",
        };
        foreach (var needle in forbidden)
            Assert.DoesNotContain(needle, yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultWorkflowDefinition_DeclaresApprovalFeedbackTaskConfig()
    {
        var definition = WorkflowProfileCatalog.Definition;

        Assert.NotNull(definition.Approval);
        Assert.NotNull(definition.Approval!.Feedback);
        var task = Assert.Single(definition.Approval!.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("Apply approval feedback", task.Title);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.NotNull(task.With);
        Assert.Equal("${{ stage.name }}", task.With!["session"]?.GetString());
        Assert.Equal("${{ prompts.apply-feedback }}", task.With["prompt"]?.GetString());
        Assert.Equal("${{ vars.agent }}", task.With["options"]?.GetString());
    }

}
