using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Domain;
using Issue = Mohist.Server.Issue.Domain.Issue;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services.WorkflowProfiles;
using Mohist.Server.UnitTests.Support;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workflow.Services.Prompts;
using Mohist.Server.Infrastructure.Data.Workflow;
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

    [Fact]
    public void BuiltInApplyFeedbackPrompt_HasRequiredFrontmatterFields()
    {
        var loader = new FilePromptLoader();
        var prompts = loader.LoadAll();

        Assert.True(prompts.ContainsKey("apply-feedback"), "apply-feedback prompt must be loaded from builtins");
        var body = prompts["apply-feedback"];
        Assert.Contains("mo issue feedback show", body, StringComparison.Ordinal);
        Assert.Contains("${{ issue.number }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ project.id }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ approvalFeedback.id }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ approvalFeedback.command }}", body, StringComparison.Ordinal);
        Assert.Contains("${{ stage.name }}", body, StringComparison.Ordinal);
        Assert.Contains("Do not approve the stage", body, StringComparison.Ordinal);
        Assert.Contains("required input", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resolution summary", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuiltInApplyFeedbackPrompt_FrontmatterParsesCleanly()
    {
        var loader = new FilePromptLoader();
        var templates = loader.LoadAllTemplates();
        var template = templates["apply-feedback"];

        Assert.Equal("apply-feedback", template.Key);
        Assert.Equal("Apply Approval Feedback", template.DisplayName);
        Assert.Contains("approval feedback", template.Description, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(template.Tags);
        Assert.Equal("approval", template.Stage);
        Assert.Contains("mo issue feedback show", template.Body, StringComparison.Ordinal);
        Assert.Contains("Do not approve the stage", template.Body, StringComparison.Ordinal);
    }
}
