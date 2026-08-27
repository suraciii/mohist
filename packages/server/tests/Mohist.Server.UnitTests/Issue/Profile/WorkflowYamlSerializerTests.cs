using System.Text.Json;
using Mohist.Workflow.Definition;
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

        Assert.Contains("unknown field 'variables'", ex.Message);
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
        var yaml = WorkflowYamlSerializer.ToYaml(WorkflowProfileCatalog.Definition);
        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);

        Assert.Equal(WorkflowProfileCatalog.Definition.Stages.Select(s => s.Stage), reparsed.Stages.Select(s => s.Stage));
        Assert.DoesNotContain("options:", yaml);
        Assert.Contains("name: mohist/planner", yaml);
        Assert.Contains("prompt: ${{ prompts.plan }}", yaml);
        Assert.DoesNotContain("repairTask:", yaml);
        Assert.DoesNotContain("prompts.auto-fix", yaml);
        var checkStage = reparsed.Stages[2];
        var aiReview = checkStage.Tasks.Single(t => t.Id == "ai-review");
        Assert.Null(aiReview.Recovery);
        Assert.Contains("PLANS/REVIEW.md", JsonSerializer.Serialize(aiReview.Artifacts));
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsPersistedProfile()
    {
        var definition = new WorkflowDefinition(
            [new StageDefinition("build", [], [])]);
        var json = JsonSerializer.Serialize(
            new WorkflowProfile("spec/workflow", "Spec", string.Empty, definition),
            WorkflowYamlSerializer.JsonOptions);

        var profile = WorkflowYamlSerializer.FromProfileJson(json);

        Assert.Equal("spec/workflow", profile.Id);
        Assert.Equal("Spec", profile.Name);
        Assert.Equal("build", Assert.Single(profile.Definition.Stages).Stage);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsTaskArtifactCapture()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
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
    public void WorkflowYamlSerializer_ValidatesLegacyInputForPiAgentAction()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks:
              - id: task
                title: Task
                uses: mohist/pi
                with:
                  agent: legacy
            checks: []
        """);
        Assert.Equal("task", definition.Stages.Single().Tasks.Single().Id);
    }

    [Fact]
    public void WorkflowYamlSerializer_EmitsPureDefinitionWithoutProfileFields()
    {
        var definition = WorkflowProfileCatalog.Definition;
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
        var yaml = WorkflowYamlSerializer.ToYaml(WorkflowProfileCatalog.Definition);

        Assert.Contains("approval:", yaml);
        Assert.Contains("feedback:", yaml);
        Assert.Contains("tasks:", yaml);
        Assert.Contains("id: apply-feedback", yaml);
        Assert.Contains("title: Apply approval feedback", yaml);
        Assert.Contains("uses: mohist/agent", yaml);
        Assert.Contains("session: ${{ stage.name }}", yaml);
        Assert.Contains("prompt: ${{ prompts.apply-feedback }}", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.NotNull(reparsed.Approval);
        Assert.NotNull(reparsed.Approval!.Feedback);
        var task = Assert.Single(reparsed.Approval!.Feedback!.Tasks!);
        Assert.Equal("apply-feedback", task.Id);
        Assert.Equal("mohist/agent", task.Uses);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsWithoutApprovalSection_WhenAbsent()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
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

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsRecoveriesSection()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        recoveries:
          rebase-conflicts:
            budget: 2
            handlers:
              - when: error.code=conflict
                tasks:
                  - id: recover:resolve-rebase-conflicts
                    title: Resolve rebase conflicts
                    uses: mohist/opencode
                    with:
                      session: check
                      prompt: ${{ prompts.resolve-rebase-conflicts }}
                      options: ${{ vars.agent }}
                retrySelf: false
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        Assert.Contains("recoveries:", yaml);
        Assert.Contains("rebase-conflicts:", yaml);
        Assert.Contains("error.code=conflict", yaml);
        Assert.Contains("recover:resolve-rebase-conflicts", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.NotNull(reparsed.Recoveries);
        Assert.True(reparsed.Recoveries!.TryGetValue("rebase-conflicts", out var recovery));
        Assert.Equal(2, recovery!.Budget);
        var handler = Assert.Single(recovery.Handlers);
        Assert.Equal("error.code=conflict", handler.When);
        Assert.False(handler.RetrySelf);
        var task = Assert.Single(handler.Tasks);
        Assert.Equal("recover:resolve-rebase-conflicts", task.Id);
        Assert.Equal("mohist/opencode", task.Uses);
    }

    [Fact]
    public void WorkflowYamlSerializer_OmitsRecoveriesSectionWhenAbsent()
    {
        var definition = WorkflowYamlSerializer.FromYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        var yaml = WorkflowYamlSerializer.ToYaml(definition);
        Assert.DoesNotContain("recoveries:", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.Null(reparsed.Recoveries);
    }

}
