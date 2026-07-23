using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public partial class ActionContractValidatorTests
{
    [Fact]
    public void Validate_MohistAgentTask_WithRequiredInputs_NoErrors_WithoutAgentState()
    {
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MohistAgentTask_AcceptsOptionalSessionAndTimeout()
    {
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(
                ("name", JsonString("reviewer")),
                ("prompt", JsonString("Audit this change.")),
                ("session", JsonString("review-thread")),
                ("timeout", JsonNumber(60_000))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MohistAgentTask_PresentInRunnerCatalog_DoesNotShadowVirtualEntry()
    {
        var catalog = new ActionCatalog(
            [new ActionCatalogEntry("mohist/agent", [], [], [], "rogue runner catalog entry")],
            []);
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MohistAgentTask_MissingName_ReportedAsRequired()
    {
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentTask_MissingPrompt_ReportedAsRequired()
    {
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prompt", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentTask_MissingNameAndPrompt_ReportedTogether()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent", With: With());
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[0].with.name" && e.Message.Contains("missing required"));
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[0].with.prompt" && e.Message.Contains("missing required"));
        Assert.All(errors, e => Assert.Equal(ValidationSource.Action, e.Source));
    }

    [Fact]
    public void Validate_MohistAgentTask_UnknownInput_ReportedAsUnknown()
    {
        var task = new TaskDefinition("review",
            Uses: "mohist/agent",
            With: With(
                ("name", JsonString("reviewer")),
                ("prompt", JsonString("Audit this change.")),
                ("runtime", JsonString("pi"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var errors = ActionContractValidator.Validate(definition, new ActionCatalog([], []));

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.runtime", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("unknown input", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentTask_TemplatePrompt_Accepted()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("${{ prompts.review }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        Assert.Empty(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
    }

    [Fact]
    public void Validate_MohistAgentTask_TemplateSession_Accepted()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(
                ("name", JsonString("reviewer")),
                ("prompt", JsonString("Audit this change.")),
                ("session", JsonString("${{ vars.reviewSession }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        Assert.Empty(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
    }

    [Fact]
    public void Validate_MohistAgentTask_TemplateTimeout_Accepted()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(
                ("name", JsonString("reviewer")),
                ("prompt", JsonString("Audit this change.")),
                ("timeout", JsonString("${{ vars.timeoutMs }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        Assert.Empty(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
    }

    [Fact]
    public void Validate_MohistAgentTask_TemplateName_Rejected()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(
                ("name", JsonString("${{ vars.agentName }}")),
                ("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("literal string", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("template", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MohistAgentTask_NonStringName_RejectedAsTypeMismatch()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(("name", JsonNumber(42)), ("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].tasks[0].with.name", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("string", error.Message);
        Assert.Contains("number", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentTask_NonStringPrompt_RejectedAsTypeMismatch()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonNumber(7))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("string", error.Message);
        Assert.Contains("number", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentTask_NonNumberTimeout_RejectedAsTypeMismatch()
    {
        var task = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(
                ("name", JsonString("reviewer")),
                ("prompt", JsonString("Audit this change.")),
                ("timeout", JsonString("30"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].tasks[0].with.timeout", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("number", error.Message);
        Assert.Contains("string", error.Message);
    }

    [Fact]
    public void Validate_MohistAgentCheck_RejectedEvenWithValidInputs()
    {
        var check = new CheckDefinition("audit", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Audit this change."))));
        var stage = new StageDefinition("build", [], [check]);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].checks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("check", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mohist/agent", error.Message);
        Assert.DoesNotContain("missing required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MohistAgentCheck_RejectedBeforeInputShapeCheck()
    {
        var check = new CheckDefinition("audit", Uses: "mohist/agent",
            With: With(("name", JsonString("${{ vars.x }}"))));
        var stage = new StageDefinition("build", [], [check]);
        var definition = new WorkflowDefinition([stage]);

        var error = Assert.Single(ActionContractValidator.Validate(definition, new ActionCatalog([], [])));
        Assert.Equal("stages[0].checks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("check", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MohistAgentTask_InRecoveryHandler_Accepted()
    {
        var innerTask = new TaskDefinition("recover-review", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Audit again."))));
        var recovery = new RecoveryDefinition(1, [new RecoveryHandlerDefinition(null, [innerTask], RetrySelf: false)]);
        var parent = new TaskDefinition("compile", Uses: "mohist/opencode",
            With: With(("prompt", JsonString("hi"))), Recovery: recovery);
        var stage = new StageDefinition("build", [parent], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode", StringInput("prompt", required: true))], []);

        Assert.Empty(ActionContractValidator.Validate(definition, catalog));
    }

    [Fact]
    public void Validate_MohistAgentTask_InApprovalFeedback_Accepted()
    {
        var feedbackTask = new TaskDefinition("revise", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Address review feedback."))));
        var stage = new StageDefinition("plan", [new("draft", Uses: "mohist/opencode",
            With: With(("prompt", JsonString("draft"))))], [], RequiresApproval: true);
        var definition = new WorkflowDefinition([stage], new ApprovalConfig(new ApprovalFeedbackConfig([feedbackTask])));
        var catalog = new ActionCatalog([CreateAction("mohist/opencode", StringInput("prompt", required: true))], []);

        Assert.Empty(ActionContractValidator.Validate(definition, catalog));
    }

    [Fact]
    public void Validate_MohistAgentTask_DoesNotInterfereWithInlineActions()
    {
        var agentTask = new TaskDefinition("review", Uses: "mohist/agent",
            With: With(("name", JsonString("reviewer")), ("prompt", JsonString("Audit this change."))));
        var inlineTask = new TaskDefinition("compile", Uses: "mohist/opencode",
            With: With(("prompt", JsonString("compile"))));
        var stage = new StageDefinition("build", [agentTask, inlineTask], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode", StringInput("prompt", required: true))], []);

        Assert.Empty(ActionContractValidator.Validate(definition, catalog));
    }
}
