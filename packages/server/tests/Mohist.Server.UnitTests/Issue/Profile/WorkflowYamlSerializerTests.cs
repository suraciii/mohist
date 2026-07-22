using System.Text.Json;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class WorkflowYamlSerializerTests
{
    [Theory]
    [InlineData("id: old")]
    [InlineData("name: old")]
    [InlineData("description: old")]
    [InlineData("variables: {}")]
    [InlineData("defaults: {}")]
    [InlineData("artifacts: {}")]
    public void WorkflowYamlSerializer_RejectsRemovedTopLevelFields(string field)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml($"{field}\nstages:\n  - stage: build\n    tasks: []\n    checks: []"));

        Assert.Contains(field.Split(':')[0], ex.Message);
    }

    [Fact]
    public void WorkflowYamlSerializer_RejectsStageVariables()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            variables: {}
            tasks: []
            checks: []
        """));

        Assert.Contains("stage field 'variables'", ex.Message);
    }

    [Fact]
    public void WorkflowYamlSerializer_JsonConversionRejectsRemovedFields()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowYamlSerializer.FromJson("""
        { "stages": [], "variables": {} }
        """));

        Assert.Contains("variables", ex.Message);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsDomainDefinition()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(MohistWorkflow.Definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.Contains("options: ${{ vars.agent }}", yaml);
        Assert.Contains("prompt: ${{ prompts.proposal }}", yaml);
        Assert.DoesNotContain("repairTask:", yaml);
        Assert.DoesNotContain("repairLimit:", yaml);
        Assert.Contains("id: recover:fix-review-findings", yaml);
        Assert.Contains("prompt: ${{ prompts.auto-fix }}", yaml);
        Assert.Contains("retrySelf: true", yaml);
        Assert.DoesNotContain("verifyTask:", yaml);
        Assert.Equal("mohist/openspec-tasks", reparsed.Stages[1].Tasks[1].Uses);
        // Review failure is modeled on the ai-review task itself
        // (failIf + with.recovery + retrySelf), not on a review-passed
        // check. The check stage no longer carries review-passed.
        var checkStage = reparsed.Stages[2];
        Assert.DoesNotContain(checkStage.Checks, c => c.Name == "review-passed");
        var aiReview = checkStage.Tasks.Single(t => t.Id == "ai-review");
        Assert.NotNull(aiReview.Recovery);
        var recovery = aiReview.Recovery!;
        Assert.Equal(2, recovery.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.True(handler.RetrySelf);
        var fixReviewFindings = Assert.Single(handler.Tasks);
        Assert.Equal("recover:fix-review-findings", fixReviewFindings.Id);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsTaskArtifactCapture()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: plan
            tasks:
              - id: declare
                title: Declare
                uses: mohist/opencode
                with:
                  prompt: hi
                artifacts:
                  files:
                    - path: docs/a.md
                    - path: docs/b.md
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        var task = reparsed.Stages.Single().Tasks.Single();
        Assert.NotNull(task.Artifacts);
        Assert.Equal(
            new[] { "docs/a.md", "docs/b.md" },
            task.Artifacts!.Files.Select(f => f.Path).ToArray());
        Assert.Contains("artifacts:", yaml);
        Assert.Contains("docs/a.md", yaml);
    }

    [Fact]
    public void WorkflowYamlSerializer_ValidatesLegacyInputForPiInlineAgent()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks:
              - id: task
                title: Task
                uses: mohist/pi
                with:
                  agent: legacy
            checks: []
        """));

        Assert.Contains("with.agent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowYamlSerializer_EmitsPureDefinitionWithoutProfileFields()
    {
        var definition = MohistWorkflow.Definition;
        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.DoesNotContain("description:", yaml);
        Assert.DoesNotContain("variables:", yaml);
        Assert.DoesNotContain("defaults:", yaml);
        Assert.DoesNotContain("\nartifacts:", yaml);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsApprovalFeedbackTaskConfig()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);

        Assert.Contains("approval:", yaml);
        Assert.Contains("feedback:", yaml);
        Assert.Contains("tasks:", yaml);
        Assert.Contains("id: apply-feedback", yaml);
        Assert.Contains("title: Apply approval feedback", yaml);
        Assert.Contains("uses: mohist/opencode", yaml);
        Assert.Contains("session: ${{ stage.name }}", yaml);
        Assert.Contains("prompt: ${{ prompts.apply-feedback }}", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.NotNull(reparsed.Approval);
        Assert.NotNull(reparsed.Approval!.Feedback);
        var task = Assert.Single(reparsed.Approval!.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("mohist/opencode", task.Uses);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsWithoutApprovalSection_WhenAbsent()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        Assert.DoesNotContain("approval:", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.Null(reparsed.Approval);
    }

}
