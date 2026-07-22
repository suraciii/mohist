using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class TypeErrorTests
{
    [Fact]
    public void Parse_RequiresApprovalNonBoolean_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                requiresApproval: "yes"
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].requiresApproval");
        Assert.Contains("must be a boolean", error.Message);
    }

    [Fact]
    public void Parse_BudgetNonInteger_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: abc
                      handlers:
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].recovery.budget");
        Assert.Contains("must be a non-negative integer", error.Message);
    }

    [Fact]
    public void Parse_BudgetNegativeInteger_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: -1
                      handlers:
                        - tasks:
                            - id: a
                              uses: a
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.budget"
            && e.Message == "recovery.budget must be a non-negative integer");
    }

    [Fact]
    public void Parse_StageNotObject_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - "not a mapping"
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0]");
        Assert.Equal("stage must be an object", error.Message);
    }

    [Fact]
    public void Parse_StagesNotList_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages: "nope"
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages");
        Assert.Equal("stages must be a list", error.Message);
    }

    [Fact]
    public void Parse_TasksNotList_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: "nope"
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks");
        Assert.Equal("stages[0].tasks must be a list", error.Message);
    }

    [Fact]
    public void Parse_RetrySelfNonBoolean_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          retrySelf: "yes"
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].retrySelf");
    }

    [Fact]
    public void Parse_UnknownFieldAtTaskLevel_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    dependsOn: t0
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].dependsOn");
        Assert.Equal("unknown field 'dependsOn'", error.Message);
    }

    [Fact]
    public void Parse_UnknownFieldAtTopLevel_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
            custom: hi
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "custom");
        Assert.Equal("unknown field 'custom'", error.Message);
    }

    [Fact]
    public void Parse_UnknownFieldAtStageLevel_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
                priority: high
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].priority");
        Assert.Equal("unknown field 'priority'", error.Message);
    }

    [Fact]
    public void Parse_NonStringScalarInStringFields_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: 123
                tasks:
                  - id: 1
                    uses: true
                    title: false
                    setVars:
                      output: 1
                    artifacts:
                      files: [false]
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "stages[0].stage" && e.Message == "'stage' must be a string");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].id" && e.Message == "'id' must be a string");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].uses" && e.Message == "'uses' must be a string");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].title" && e.Message == "'title' must be a string");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].setVars.output" && e.Message == "setVars value for 'output' must be a string");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].artifacts.files[0]" && e.Message == "artifacts.files[] entry must be a string");
    }

    [Fact]
    public void Parse_LeadingZeroNumberScalarInStringField_RejectedWithoutCrash()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: 001
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].stage" && e.Message == "'stage' must be a string");
    }

    [Fact]
    public void Parse_LeadingZeroNumberScalarInWith_DoesNotCrash()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      count: 001
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_NonDecimalYamlNumberScalarsInStringFields_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: 0x10
                tasks:
                  - id: 1_000
                    uses: action
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].stage" && e.Message == "'stage' must be a string");
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].id" && e.Message == "'id' must be a string");
    }

    [Fact]
    public void Parse_NonDecimalYamlNumberInWith_DoesNotCrash()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: action
                    with:
                      hexadecimal: 0x10
                      underscored: 1_000
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_QuotedScalarForTypedFields_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                requiresApproval: "true"
                tasks:
                  - id: t1
                    uses: action
                    recovery:
                      budget: "1"
                      handlers:
                        - retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "stages[0].requiresApproval");
        Assert.Contains(result.Errors, e => e.Path == "stages[0].tasks[0].recovery.budget");
    }

    [Fact]
    public void Parse_MultipleDocuments_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
            ---
            unknown: true
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("yaml must contain exactly one document", error.Message);
    }

    [Fact]
    public void Parse_UnknownFieldAtHandlerLevel_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          retrySelf: true
                          retryself: true
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].retryself");
        Assert.Equal("unknown field 'retryself'", error.Message);
    }
}
