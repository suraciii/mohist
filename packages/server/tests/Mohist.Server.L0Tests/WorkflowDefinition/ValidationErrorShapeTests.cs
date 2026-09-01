using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class ValidationErrorShapeTests
{
    [Fact]
    public void ValidationError_DefaultSource_IsDefinition()
    {
        var error = new ValidationError("stages[0]", "stages must be non-empty");

        Assert.Equal("stages[0]", error.Path);
        Assert.Equal("stages must be non-empty", error.Message);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    [Fact]
    public void ValidationError_ExplicitActionSource_Supported()
    {
        var error = new ValidationError(
            "stages[0].tasks[0].with.agent",
            "with.agent is rejected for Action contract reasons",
            ValidationSource.Action);

        Assert.Equal(ValidationSource.Action, error.Source);
    }

    [Fact]
    public void ValidationError_ToString_PathColonMessage()
    {
        var error = new ValidationError("stages[0]", "stages must be non-empty");

        Assert.Equal("stages[0]: stages must be non-empty", error.ToString());
    }

    [Fact]
    public void Parse_Path_ArrayIndicesUseBrackets()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: a
                    recovery:
                      budget: -1
                      handlers:
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        var budgetError = result.Errors.First(e => e.Path == "stages[0].tasks[0].recovery.budget");
        Assert.Equal("stages[0].tasks[0].recovery.budget", budgetError.Path);
    }

    [Fact]
    public void Parse_Errors_FromSecondRecoveryHandlerOfFirstTaskOfSecondStage_UseCorrectPath()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                checks: []
              - stage: check
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
                checks: []
            """);

        Assert.Contains(result.Errors, e =>
            e.Path == "stages[1].tasks[0].recovery.handlers[1]");
    }

    [Fact]
    public void Parse_Message_NeverContainsTypeNames()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages: []
            """);

        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain("TaskDefinition", error.Message);
            Assert.DoesNotContain("StageDefinition", error.Message);
            Assert.DoesNotContain("WorkflowDefinition", error.Message);
            Assert.DoesNotContain("YamlStream", error.Message);
        });
    }
}
