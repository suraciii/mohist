using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class IdentifierRulesTests
{
    [Fact]
    public void Parse_TaskMissingUses_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].uses");
        Assert.Equal("uses is required", error.Message);
    }

    [Fact]
    public void Parse_TaskMissingId_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - uses: mohist/opencode
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].id");
        Assert.Equal("task identifier is required", error.Message);
    }

    [Fact]
    public void Parse_DuplicateTaskIds_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: same
                    uses: a
                  - id: same
                    uses: b
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[1].id");
        Assert.Equal("task identifier 'same' is duplicated", error.Message);
    }

    [Fact]
    public void Parse_DuplicateTaskIdsAcrossRecoveryHandlers_AlsoDetected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: outer
                    uses: mohist/opencode
                    recovery:
                      budget: 1
                      handlers:
                        - when: error.code=conflict
                          tasks:
                            - id: collide
                              uses: a
                            - id: collide
                              uses: b
                          retrySelf: true
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].recovery.handlers[0].tasks[1].id");
    }

    [Fact]
    public void Parse_TaskTitleOptional_SucceedsWhenAbsent()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.Null(result.Definition!.Stages[0].Tasks[0].Title);
    }

    [Fact]
    public void Parse_TaskTitleOptional_SucceedsWhenPresent()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    title: Hello
                    uses: mohist/opencode
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.Equal("Hello", result.Definition!.Stages[0].Tasks[0].Title);
    }

    [Fact]
    public void Parse_DuplicateStageIds_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[1].stage");
        Assert.Equal("stage identifier 'build' is duplicated", error.Message);
    }

    [Fact]
    public void Parse_EmptyStageId_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: ""
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].stage");
        Assert.Equal("stage identifier is required", error.Message);
    }

    [Fact]
    public void Parse_DuplicateCheckIds_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
                    uses: a
                  - id: lint
                    uses: b
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].checks[1].id");
        Assert.Equal("check identifier 'lint' is duplicated", error.Message);
    }

    [Fact]
    public void Parse_CheckMissingId_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - uses: mohist/opencode
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].checks[0].id");
        Assert.Equal("check identifier is required", error.Message);
    }

    [Fact]
    public void Parse_CheckMissingUses_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].checks[0].uses");
        Assert.Equal("uses is required", error.Message);
    }
}
