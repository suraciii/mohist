using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class ValidateModelTests
{
    [Fact]
    public void Validate_NullDefinition_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkflowDefinitionValidator.Validate(null!));
    }

    [Fact]
    public void Validate_EmptyStages_ReturnsError()
    {
        var definition = new WorkflowDefinition(Array.Empty<StageDefinition>());

        var errors = WorkflowDefinitionValidator.Validate(definition);

        var error = Assert.Single(errors);
        Assert.Equal("stages", error.Path);
        Assert.Equal("stages must be non-empty", error.Message);
    }

    [Fact]
    public void Validate_DetectsDuplicateStageIds()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("build", [], []),
            new StageDefinition("build", [], []),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(errors, e =>
            e.Path == "stages[1].stage"
            && e.Message == "stage identifier 'build' is duplicated");
    }

    [Fact]
    public void Validate_DetectsMissingUses()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition(
                "build",
                [new TaskDefinition(Id: "t1", Uses: "")],
                []),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(errors, e =>
            e.Path == "stages[0].tasks[0].uses");
    }

    [Fact]
    public void Validate_DetectsUnknownTaskFieldInModel()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition(
                "build",
                [new TaskDefinition(Id: "t1", Uses: "a", Title: "T", With: new Dictionary<string, System.Text.Json.JsonElement?>
                {
                    ["extra"] = System.Text.Json.JsonDocument.Parse("1").RootElement,
                })],
                []),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_Accepts_ValidDefinition()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition(
                "build",
                [new TaskDefinition(Id: "t1", Uses: "a")],
                [new CheckDefinition(Id: "lint", Uses: "core/lint")]),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AndParse_ShareRules_ForStageIdentifierRules()
    {
        var parseResult = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
              - stage: build
                tasks: []
                checks: []
            """);

        var modelErrors = WorkflowDefinitionValidator.Validate(parseResult.Definition!);

        Assert.NotEmpty(modelErrors);
        Assert.Contains(modelErrors, e => e.Path == "stages[1].stage");
        Assert.Equal(parseResult.Errors.First(e => e.Path == "stages[1].stage").Message,
                     modelErrors.First(e => e.Path == "stages[1].stage").Message);
    }

    [Fact]
    public void Validate_AndParse_ShareRules_ForRecoveryStructure()
    {
        var parseResult = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                        - tasks:
                            - id: b
                              uses: b
                          retrySelf: true
                checks: []
            """);

        var modelErrors = WorkflowDefinitionValidator.Validate(parseResult.Definition!);

        Assert.NotEmpty(modelErrors);
        Assert.Contains(modelErrors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[1]");
    }

    [Fact]
    public void Validate_AndParse_ReturnSameSourceForSameError()
    {
        var parseResult = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                checks: []
            """);

        var modelErrors = WorkflowDefinitionValidator.Validate(parseResult.Definition!);

        var parseError = parseResult.Errors.First(e => e.Path == "stages[0].tasks[0].uses");
        var validateError = modelErrors.First(e => e.Path == "stages[0].tasks[0].uses");
        Assert.Equal(ValidationSource.Definition, parseError.Source);
        Assert.Equal(ValidationSource.Definition, validateError.Source);
    }

    [Fact]
    public void Validate_LockBehaviorRules_Accepted()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("integrate", [], [], LockBehavior: "sequential", Resources: ["r1"]),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_LockBehaviorRules_Rejected()
    {
        var definition = new WorkflowDefinition(new[]
        {
            new StageDefinition("integrate", [], [], LockBehavior: "sequential"),
        });

        var errors = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(errors, e =>
            e.Path == "stages[0].lockBehavior"
            && e.Message == "lockBehavior requires non-empty resources");
    }
}
