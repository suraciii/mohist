using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Server.UnitTests.Workflow.Services;

public class ActionContractValidatorTests
{
    private static ActionCatalogEntry CreateAction(
        string name,
        params ActionCatalogInput[] inputs) =>
        new(name, inputs, [], []);

    private static ActionCatalogInput StringInput(string name, bool required = false) =>
        new(name, ["string"], required);

    private static ActionCatalogInput NumberInput(string name, bool required = false) =>
        new(name, ["number"], required);

    private static ActionCatalogInput UnionInput(string name, string[] kinds, bool required = false) =>
        new(name, kinds, required);

    private static ActionCatalogTombstone CreateTombstone(string name, string guidance) =>
        new(name, guidance);

    private static JsonElement? JsonString(string text) =>
        JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();

    private static JsonElement? JsonNumber(double value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static JsonElement? JsonBoolean(bool value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static JsonElement? JsonObject(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement? JsonNull() =>
        JsonDocument.Parse("null").RootElement.Clone();

    private static Dictionary<string, JsonElement?> With(params (string Key, JsonElement? Value)[] entries)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            result[key] = value;
        }
        return result;
    }

    [Fact]
    public void Validate_NullDefinition_ReturnsEmpty()
    {
        var catalog = new ActionCatalog([], []);

        var errors = ActionContractValidator.Validate(null, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_EmptyStages_NoErrors()
    {
        var definition = new WorkflowDefinition([]);
        var catalog = new ActionCatalog([], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_UnknownUsesInStageTask_NamesTaskIdActionPathAndActionSource()
    {
        var task = new TaskDefinition("compile", Uses: "mohist/opencodee");
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("compile", error.Message);
        Assert.Contains("mohist/opencodee", error.Message);
    }

    [Fact]
    public void Validate_UnknownUsesInStageCheck_UsesCheckPath()
    {
        var check = new CheckDefinition("lint", Uses: "mohist/unknown");
        var stage = new StageDefinition("build", [], [check]);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].checks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("Check", error.Message);
        Assert.Contains("lint", error.Message);
        Assert.Contains("mohist/unknown", error.Message);
    }

    [Fact]
    public void Validate_UnknownUsesInApprovalFeedbackTask_UsesFeedbackPath()
    {
        var feedbackTask = new TaskDefinition("revise", Uses: "mohist/opencodee");
        var feedback = new ApprovalFeedbackConfig([feedbackTask]);
        var stage = new StageDefinition("plan", [new("draft", Uses: "mohist/opencode")], [], RequiresApproval: true);
        var definition = new WorkflowDefinition([stage], new ApprovalConfig(feedback));
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("approval.feedback.tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("revise", error.Message);
        Assert.Contains("mohist/opencodee", error.Message);
    }

    [Fact]
    public void Validate_UnknownUsesInRecoveryHandlerTask_UsesRecoveryPath()
    {
        var innerTask = new TaskDefinition("recover", Uses: "mohist/opencodee");
        var handler = new RecoveryHandlerDefinition(null, [innerTask], RetrySelf: false);
        var recovery = new RecoveryDefinition(1, [handler]);
        var task = new TaskDefinition("compile", Uses: "mohist/opencode", Recovery: recovery);
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].recovery.handlers[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("recover", error.Message);
        Assert.Contains("mohist/opencodee", error.Message);
    }

    [Fact]
    public void Validate_TombstonedUses_ProducesRemovedMessageWithGuidance()
    {
        var task = new TaskDefinition("compile", Uses: "mohist/acp-agent");
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([], [CreateTombstone("mohist/acp-agent", "use mohist/opencode instead")]);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("removed", error.Message);
        Assert.Contains("mohist/acp-agent", error.Message);
        Assert.Contains("use mohist/opencode instead", error.Message);
    }

    [Fact]
    public void Validate_TombstonedAndUnknown_ProduceDistinguishableMessages()
    {
        var tombstoned = new TaskDefinition("compile", Uses: "mohist/acp-agent");
        var unknown = new TaskDefinition("lint", Uses: "mohist/never-existed");
        var stage = new StageDefinition("build", [tombstoned, unknown], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([], [CreateTombstone("mohist/acp-agent", "moved to mohist/opencode")]);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Equal(2, errors.Count);
        var removed = errors.Single(e => e.Message.Contains("mohist/acp-agent"));
        var unknownError = errors.Single(e => e.Message.Contains("mohist/never-existed"));
        Assert.Contains("removed", removed.Message);
        Assert.Contains("moved to mohist/opencode", removed.Message);
        Assert.Contains("unknown", unknownError.Message);
        Assert.DoesNotContain("removed", unknownError.Message);
    }

    [Fact]
    public void Validate_UnknownWithField_NamesFieldAndAction()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("command", JsonString("run")), ("commmand", JsonString("typo"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("command", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.commmand", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("commmand", error.Message);
        Assert.Contains("mohist/opencode", error.Message);
    }


    [Fact]
    public void Validate_WorkingDirectory_IsNotTreatedAsUnknown()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("working-directory", JsonString("./repo")), ("prompt", JsonString("hi"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MissingRequiredInput_NamesTheField()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With());
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("prompt", error.Message);
    }

    [Fact]
    public void Validate_OptionalInputOmitted_NoError()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/rebase",
            With: With(("baseBranch", JsonString("master"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/rebase",
                StringInput("baseBranch", required: true),
                StringInput("remote", required: false))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NumericStringForNumberInput_RejectedWithActualKind()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/wait",
            With: With(("timeout", JsonString("30"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/wait", NumberInput("timeout", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.timeout", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("timeout", error.Message);
        Assert.Contains("number", error.Message);
        Assert.Contains("string", error.Message);
    }

    [Fact]
    public void Validate_ObjectForStringInput_RejectedWithoutSerialization()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("message", JsonObject("""{ "nested": true }"""))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("message", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Contains("message", error.Message);
        Assert.Contains("string", error.Message);
        Assert.Contains("object", error.Message);
    }

    [Fact]
    public void Validate_UnionKind_StringAccepted()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonString("hello"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode",
                UnionInput("prompt", ["string", "object"], required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_UnionKind_ObjectAccepted()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonObject("""{ "key": "value" }"""))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode",
                UnionInput("prompt", ["string", "object"], required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TypeMismatch_ListAcceptedKindsInCanonicalOrder()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/multi",
            With: With(("value", JsonBoolean(true))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/multi",
                UnionInput("value", ["string", "object", "number"], required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Contains("string or number or object", error.Message);
        Assert.Contains("boolean", error.Message);
    }

    [Fact]
    public void Validate_OptionalInputExplicitNull_TreatedAsAbsent()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/rebase",
            With: With(
                ("baseBranch", JsonString("master")),
                ("remote", JsonNull())));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/rebase",
                StringInput("baseBranch", required: true),
                StringInput("remote", required: false))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RequiredInputExplicitNull_RejectedAsTypeMismatch()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonNull())));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.prompt", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("string", error.Message);
        Assert.Contains("null", error.Message);
    }

    [Fact]
    public void Validate_TemplateValuedDeclaredInput_AcceptedWithoutTypeAssertion()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonString("${{ vars.buildPrompt }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TemplateValueOnUndeclaredField_StillRejectedByName()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(
                ("prompt", JsonString("${{ vars.buildPrompt }}")),
                ("ghost", JsonString("${{ vars.x }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0].with.ghost", error.Path);
        Assert.Equal(ValidationSource.Action, error.Source);
        Assert.Contains("ghost", error.Message);
        Assert.Contains("unknown", error.Message);
    }

    [Fact]
    public void Validate_TemplateValueMixedWithStringValue_AcceptsTemplateSkipsString()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(
                ("prompt", JsonString("${{ vars.buildPrompt }}")),
                ("message", JsonString("plain text"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode",
                StringInput("prompt", required: true),
                StringInput("message", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_TemplateValueOnNumberInput_AcceptedWithoutNumberAssertion()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/wait",
            With: With(("timeout", JsonString("${{ vars.timeoutSeconds }}"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/wait", NumberInput("timeout", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AllFourPositionsAreJudged()
    {
        var stageTask = new TaskDefinition("stage-task", Uses: "mohist/missing");
        var stageCheck = new CheckDefinition("stage-check", Uses: "mohist/missing");
        var feedbackTask = new TaskDefinition("feedback-task", Uses: "mohist/missing");
        var innerTask = new TaskDefinition("recovery-task", Uses: "mohist/missing");
        var handler = new RecoveryHandlerDefinition(null, [innerTask], RetrySelf: false);
        var recovery = new RecoveryDefinition(1, [handler]);
        var recoveryParent = new TaskDefinition("recovery-parent", Uses: "mohist/opencode", Recovery: recovery);

        var stage = new StageDefinition("build", [stageTask, recoveryParent], [stageCheck]);
        var feedback = new ApprovalFeedbackConfig([feedbackTask]);
        var definition = new WorkflowDefinition([stage], new ApprovalConfig(feedback));
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[0]");
        Assert.Contains(errors, e => e.Path == "stages[0].checks[0]");
        Assert.Contains(errors, e => e.Path == "approval.feedback.tasks[0]");
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[1].recovery.handlers[0].tasks[0]");
        Assert.All(errors, e => Assert.Equal(ValidationSource.Action, e.Source));
    }

    [Fact]
    public void Validate_DefinitionOnlyError_LeavesValidatorSilent()
    {
        var task = new TaskDefinition("", Uses: "mohist/opencode");
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MultipleErrorsAcrossTasks_AreAllReported()
    {
        var task1 = new TaskDefinition("task-1",
            Uses: "mohist/missing",
            With: With());
        var task2 = new TaskDefinition("task-2",
            Uses: "mohist/opencode",
            With: With(("agent", JsonString("legacy"))));
        var stage = new StageDefinition("build", [task1, task2], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[0]"
            && e.Message.Contains("unknown")
            && e.Message.Contains("mohist/missing"));
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[1].with.agent"
            && e.Message.Contains("unknown input"));
        Assert.Contains(errors, e => e.Path == "stages[0].tasks[1].with.prompt"
            && e.Message.Contains("missing required"));
    }

    [Fact]
    public void Validate_TaskIdMissing_UsesPathAsIdentifier()
    {
        var task = new TaskDefinition("", Uses: "mohist/missing");
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog([CreateAction("mohist/opencode")], []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].tasks[0]", error.Path);
        Assert.Contains("stages[0].tasks[0]", error.Message);
        Assert.Contains("mohist/missing", error.Message);
    }

    [Fact]
    public void Validate_MultipleUnknownKinds_InCanonicalOrder()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/multi",
            With: With(("value", JsonObject("""{ "k": "v" }"""))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/multi",
                UnionInput("value", ["array", "string", "number", "boolean"], required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Contains("string or number or boolean or array", error.Message);
        Assert.Contains("object", error.Message);
    }

    [Fact]
    public void Validate_ArrayValueForArrayInput_Accepted()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/list",
            With: With(("items", JsonObject("""[ "a", "b" ]"""))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/list", UnionInput("items", ["array"], required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_StageCheck_NotInTaskLoopAndTask_NotInCheckLoop()
    {
        var taskWithWith = new TaskDefinition("compile",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonString("hello"))));
        var checkWithWith = new CheckDefinition("lint",
            Uses: "mohist/opencode",
            With: With(("prompt", JsonString("hello"))));
        var stage = new StageDefinition("build", [taskWithWith], [checkWithWith]);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_NumericStringForNumberInCheck_Rejected()
    {
        var check = new CheckDefinition("lint",
            Uses: "mohist/wait",
            With: With(("timeout", JsonString("30"))));
        var stage = new StageDefinition("build", [], [check]);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/wait", NumberInput("timeout", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var error = Assert.Single(errors);
        Assert.Equal("stages[0].checks[0].with.timeout", error.Path);
        Assert.Contains("number", error.Message);
        Assert.Contains("string", error.Message);
    }

    [Fact]
    public void Validate_UnknownUsesAndUnknownWith_BothReported()
    {
        var task = new TaskDefinition("compile",
            Uses: "mohist/missing",
            With: With(("agent", JsonString("legacy"))));
        var stage = new StageDefinition("build", [task], []);
        var definition = new WorkflowDefinition([stage]);
        var catalog = new ActionCatalog(
            [CreateAction("mohist/opencode", StringInput("prompt", required: true))],
            []);

        var errors = ActionContractValidator.Validate(definition, catalog);

        var unknownUses = errors.Single(e => e.Message.Contains("unknown Action"));
        Assert.Equal("stages[0].tasks[0]", unknownUses.Path);
    }
}
