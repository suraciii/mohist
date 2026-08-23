using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class AuxiliaryDeclarationsTests
{
    [Fact]
    public void Parse_SetVarsValueWithoutOutputPrefix_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    setVars:
                      result: status
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e => e.Path == "stages[0].tasks[0].setVars.result");
        Assert.Equal(
            "setVars value must be an output.* path (got 'status')",
            error.Message);
    }

    [Fact]
    public void Parse_SetVarsValueWithOutputPrefix_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    setVars:
                      changeId: output.changeId
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_SetVarsEmptyKey_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    setVars:
                      "": output.value
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_ExpectMarkerFailIfNotMemberOfOneOf_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      markers:
                        - path: result.md
                          oneOf:
                            - pass
                            - fail
                          failIf: maybe
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = result.Errors.Single(e =>
            e.Path == "stages[0].tasks[0].expect.markers[0].failIf");
        Assert.Equal("expect.markers[].failIf must be a member of oneOf (got 'maybe')", error.Message);
    }

    [Fact]
    public void Parse_ExpectMarkerEmptyOneOf_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      markers:
                        - path: result.md
                          oneOf: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].expect.markers[0].oneOf");
    }

    [Fact]
    public void Parse_ExpectMarkerOneOfContainingFailIf_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      markers:
                        - path: result.md
                          oneOf:
                            - pass
                            - fail
                          failIf: fail
                checks: []
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Parse_ArtifactsFileEmptyPath_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    artifacts:
                      files:
                        - path: ""
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].artifacts.files[0].path");
    }

    [Fact]
    public void Parse_ArtifactsFileWithPath_Accepted()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    artifacts:
                      files:
                        - path: out.md
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.Single(result.Definition!.Stages[0].Tasks[0].Artifacts!.Files);
    }

    [Fact]
    public void Parse_ExpectFileEmptyPath_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      files:
                        - path: ""
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].expect.files[0].path");
    }

    [Fact]
    public void Parse_ExpectFileNonStringPath_Rejected()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                    uses: mohist/opencode
                    expect:
                      files:
                        - other: result.md
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Path == "stages[0].tasks[0].expect.files[0].path");
    }
}
