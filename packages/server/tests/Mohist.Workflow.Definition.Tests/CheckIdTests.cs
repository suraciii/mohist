using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class CheckIdTests
{
    [Fact]
    public void Parse_CheckUsesIdField_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
                    uses: mohist/opencode
            """);

        Assert.True(result.IsValid);
        var check = Assert.Single(result.Definition!.Stages[0].Checks);
        Assert.Equal("lint", check.Id);
    }

    [Fact]
    public void Parse_CheckUsingName_Rejected_AsUnknownField()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - name: lint
                    uses: mohist/opencode
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].checks[0].name" && e.Message == "unknown field 'name'");
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].checks[0].id" && e.Message == "check identifier is required");
    }

    [Fact]
    public void Parse_CheckUsingBothIdAndName_OnlyIdIsRecognized()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks:
                  - id: lint
                    name: legacy
                    uses: mohist/opencode
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].checks[0].name");
    }
}
