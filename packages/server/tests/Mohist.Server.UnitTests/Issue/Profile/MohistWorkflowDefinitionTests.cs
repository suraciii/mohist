using System.Text.Json;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistWorkflowDefinitionTests
{
    [Fact]
    public void DefaultWorkflowDefinition_UsesWorkspacePlanArtifactsAndEvidenceReview()
    {
        var definition = WorkflowProfileCatalog.Definition;
        Assert.Equal(["plan", "build", "check", "integrate"], definition.Stages.Select(s => s.Stage).ToArray());
        var plan = definition.Stages[0];
        Assert.Equal(["workspace-prepare", "plan"], plan.Tasks.Select(t => t.Id).ToArray());
        Assert.Contains("PLANS/PLAN.md", JsonSerializer.Serialize(plan.Tasks[1].Expect));
        Assert.Contains("PLANS/DESIGN.md", JsonSerializer.Serialize(plan.Tasks[1].Artifacts));
        Assert.Contains("PLANS/tasks.json", JsonSerializer.Serialize(plan.Tasks[1].Artifacts));
        var load = definition.Stages[1].Tasks.Single(t => t.Id == "load-tasks");
        Assert.Equal("mohist/task-list", load.Uses);
        Assert.Contains("PLANS/tasks.json", JsonSerializer.Serialize(load.With));
        var review = definition.Stages[2].Tasks.Single(t => t.Id == "ai-review");
        Assert.Contains("PLANS/REVIEW.md", JsonSerializer.Serialize(review.Expect));
        Assert.Null(review.Recovery);
    }

    [Fact]
    public void DefaultWorkflowDefinition_RepositoryTasksUseNestedCheckout()
    {
        var definition = WorkflowProfileCatalog.Definition;
        var repositoryUses = new HashSet<string>(StringComparer.Ordinal) { "core/script", "mohist/workspace-prepare", "mohist/rebase", "mohist/push", "mohist/merge-ready" };
        foreach (var task in definition.Stages.SelectMany(s => s.Tasks).Where(t => repositoryUses.Contains(t.Uses)))
            Assert.Equal("REPOS/${{ repository.name }}", task.With!["working-directory"]!.Value.GetString());
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
        Assert.Equal("mohist/agent", task.Uses);
        Assert.Equal("mohist/builder", task.With!["name"]!.Value.GetString());
        Assert.NotNull(task.With);
        Assert.Equal("feedback-${{ stage.name }}", task.With!["session"]?.GetString());
        Assert.Equal("${{ prompts.apply-feedback }}", task.With["prompt"]?.GetString());
        Assert.False(task.With!.ContainsKey("options"));
    }

    [Fact]
    public void DefaultWorkflowDefinition_BuildStageUsesOneProjectVerificationTask()
    {
        var build = WorkflowProfileCatalog.Definition.Stages.Single(s => s.Stage == "build");
        var verify = build.Tasks.Single(t => t.Id == "verify");

        Assert.Equal("core/script", verify.Uses);
        Assert.Equal("${{ workflow.verification.command }}", ReadString(build, "verify"));
        Assert.Equal("REPOS/${{ repository.name }}", verify.With!["working-directory"]!.Value.GetString());
        Assert.Equal("900000", verify.With!["timeout"]!.Value.GetRawText());
        AssertFixCiRecovery(verify);
        Assert.Equal(2, verify.Recovery!.Handlers.Count);
        Assert.Equal("error.code=script-failed", verify.Recovery.Handlers[0].When);
        Assert.Equal("error.code=timeout", verify.Recovery.Handlers[1].When);
    }

    [Fact]
    public void GithubPrWorkflowDefinition_UsesOneProjectVerificationTask()
    {
        var build = WorkflowProfileCatalog.GithubPrWorkflowDefinition.Stages.Single(s => s.Stage == "build");
        var verify = build.Tasks.Single(t => t.Id == "verify");
        Assert.Equal("${{ workflow.verification.command }}", ReadString(build, "verify"));
        Assert.Equal(2, verify.Recovery!.Handlers.Count);
        Assert.All(verify.Recovery.Handlers, handler =>
        {
            Assert.True(handler.RetrySelf);
            var fixCi = Assert.Single(handler.Tasks);
            Assert.Equal("recover:fix-ci", fixCi.Id);
            Assert.Equal("mohist/agent", fixCi.Uses);
            Assert.Equal("mohist/builder", fixCi.With!["name"]!.Value.GetString());
            Assert.Null(fixCi.Expect);
        });
    }

    private static string? ReadString(StageDefinition stage, string taskId)
    {
        var task = stage.Tasks.Single(t => t.Id == taskId);
        return task.With!.TryGetValue("run", out var run) && run.HasValue
            ? (run.Value.ValueKind == JsonValueKind.String ? run.Value.GetString() : run.Value.GetRawText())
            : null;
    }

    private static void AssertFixCiRecovery(Mohist.Workflow.Definition.TaskDefinition lane)
    {
        var recovery = lane.Recovery;
        Assert.NotNull(recovery);
        Assert.Equal(2, recovery!.Budget);
        var handler = recovery.Handlers.Single(handler => handler.When == "error.code=script-failed");
        Assert.True(handler.RetrySelf);
        var fixCi = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-ci", fixCi.Id);
        Assert.Equal("Fix CI verification", fixCi.Title);
        Assert.Equal("mohist/agent", fixCi.Uses);
        Assert.Equal("mohist/builder", fixCi.With!["name"]!.Value.GetString());
        Assert.Equal("${{ prompts.fix-ci }}", fixCi.With!["prompt"]!.Value.GetString());
        Assert.False(fixCi.With!.ContainsKey("options"));
        Assert.Equal("build", fixCi.With!["session"]!.Value.GetString());

        // The local profile's recovery declaration keeps the existing
        // expected-output markers (done/unfinished) on the fix-ci helper.
        var expectElement = JsonSerializer.SerializeToElement(fixCi.Expect);
        Assert.True(expectElement.TryGetProperty("markers", out var markers));
        var marker = Assert.Single(markers.EnumerateArray());
        Assert.Equal("_output", marker.GetProperty("path").GetString());
        var oneOf = marker.GetProperty("oneOf").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("<promise>done</promise>", oneOf);
        Assert.Contains("<promise>unfinished</promise>", oneOf);
    }
}
