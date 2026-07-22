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
