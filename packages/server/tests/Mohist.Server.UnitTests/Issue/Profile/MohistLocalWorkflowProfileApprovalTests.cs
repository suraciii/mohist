using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Issue.Profile;

public class MohistLocalWorkflowProfileApprovalTests
{
    [Fact]
    public void WorkflowYamlParser_ParsesApprovalFeedbackTaskConfig()
    {
        var definition = MohistWorkflow.ParseYaml("""
        approval:
          feedback:
            task:
              id: apply-feedback
              title: Apply approval feedback
              uses: mohist/acp-agent
              with:
                session: ${{ stage.name }}
                prompt: ${{ prompts.apply-feedback }}
        stages:
          - stage: plan
            tasks: []
            checks: []
        """);

        Assert.NotNull(definition.Approval);
        Assert.NotNull(definition.Approval!.Feedback);
        var task = definition.Approval!.Feedback!.Task;
        Assert.NotNull(task);
        Assert.Equal("apply-feedback", task!.Id);
        Assert.Equal("Apply approval feedback", task.Title);
        Assert.Equal("mohist/acp-agent", task.Uses);
        Assert.NotNull(task.With);
        Assert.True(task.With!.ContainsKey("session"));
        Assert.True(task.With!.ContainsKey("prompt"));
        Assert.Equal("${{ stage.name }}", task.With["session"]?.GetString());
        Assert.Equal("${{ prompts.apply-feedback }}", task.With["prompt"]?.GetString());
    }

    [Fact]
    public void DefaultWorkflowDefinition_DeclaresApprovalFeedbackTaskConfig()
    {
        var definition = MohistWorkflow.Definition;

        Assert.NotNull(definition.Approval);
        Assert.NotNull(definition.Approval!.Feedback);
        var task = definition.Approval!.Feedback!.Task;
        Assert.NotNull(task);
        Assert.Equal("apply-feedback", task!.Id);
        Assert.Equal("Apply approval feedback", task.Title);
        Assert.Equal("mohist/acp-agent", task.Uses);
        Assert.NotNull(task.With);
        Assert.Equal("${{ stage.name }}", task.With!["session"]?.GetString());
        Assert.Equal("${{ prompts.apply-feedback }}", task.With["prompt"]?.GetString());
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalSectionAbsent_ReturnsNullApproval()
    {
        var definition = MohistWorkflow.ParseYaml("""
        stages:
          - stage: build
            tasks: []
            checks: []
        """);

        Assert.Null(definition.Approval);
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalFeedbackTaskMissingId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        approval:
          feedback:
            task:
              title: Apply approval feedback
              uses: mohist/acp-agent
        stages:
          - stage: build
            tasks: []
            checks: []
        """));

        Assert.Contains("approval.feedback.task", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void WorkflowYamlParser_ApprovalFeedbackTaskMissingTitle_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MohistWorkflow.ParseYaml("""
        approval:
          feedback:
            task:
              id: apply-feedback
              uses: mohist/acp-agent
        stages:
          - stage: build
            tasks: []
            checks: []
        """));

        Assert.Contains("approval.feedback.task", ex.Message);
        Assert.Contains("title", ex.Message);
    }

    [Fact]
    public void WorkflowYamlSerializer_RoundTripsApprovalFeedbackTaskConfig()
    {
        var yaml = WorkflowYamlSerializer.ToYaml(MohistWorkflow.Definition);

        Assert.Contains("approval:", yaml);
        Assert.Contains("feedback:", yaml);
        Assert.Contains("task:", yaml);
        Assert.Contains("id: apply-feedback", yaml);
        Assert.Contains("title: Apply approval feedback", yaml);
        Assert.Contains("uses: mohist/acp-agent", yaml);
        Assert.Contains("session: ${{ stage.name }}", yaml);
        Assert.Contains("prompt: ${{ prompts.apply-feedback }}", yaml);

        var reparsed = WorkflowYamlSerializer.FromYaml(yaml);
        Assert.NotNull(reparsed.Approval);
        Assert.NotNull(reparsed.Approval!.Feedback);
        var task = reparsed.Approval!.Feedback!.Task;
        Assert.NotNull(task);
        Assert.Equal("apply-feedback", task!.Id);
        Assert.Equal("mohist/acp-agent", task.Uses);
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
