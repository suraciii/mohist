using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class WithOpenStructureTests
{
    [Fact]
    public void Parse_WithAbsent_Accepted()
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
        Assert.Null(result.Definition!.Stages[0].Tasks[0].With);
    }

    [Fact]
    public void Parse_WithObject_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      prompt: hello
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Definition!.Stages[0].Tasks[0].With);
    }

    [Fact]
    public void Parse_WithUnknownKey_NotAnError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      anything: 1
                      something: nested
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, e => e.Path.Contains(".with."));
    }

    [Fact]
    public void Parse_WithPlainString_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with: "plain string"
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].with");
        Assert.Equal("stages[0].tasks[0].with must be an object", error.Message);
    }

    [Fact]
    public void Parse_WithList_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      - foo
                      - bar
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].with");
        Assert.Equal("stages[0].tasks[0].with must be an object", error.Message);
    }

    [Fact]
    public void Parse_CheckWithUnknownKey_NotAnError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
                    uses: mohist/opencode
                    with:
                      completelyUnknownKey: true
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_WithNestedUnknownKeys_NotAnError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    with:
                      nested:
                        deep:
                          anything: 1
                checks: []
            """);

        Assert.True(result.IsValid);
    }
}
