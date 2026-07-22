using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class TopLevelStructureTests
{
    [Fact]
    public void Parse_Rejects_TopLevelMetadata()
    {
        var result = WorkflowDefinitionParser.Parse("""
            id: my-profile
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("id", error.Path);
        Assert.Equal("unknown field 'id'", error.Message);
    }

    [Fact]
    public void Parse_Rejects_TopLevelName()
    {
        var result = WorkflowDefinitionParser.Parse("""
            name: my-profile
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("name", error.Path);
        Assert.Equal("unknown field 'name'", error.Message);
    }

    [Fact]
    public void Parse_Rejects_TopLevelDescription()
    {
        var result = WorkflowDefinitionParser.Parse("""
            description: some description
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("description", error.Path);
        Assert.Equal("unknown field 'description'", error.Message);
    }

    [Fact]
    public void Parse_Rejects_TopLevelVariables()
    {
        var result = WorkflowDefinitionParser.Parse("""
            variables: {}
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("variables", error.Path);
        Assert.Equal("unknown field 'variables'", error.Message);
    }

    [Fact]
    public void Parse_Rejects_TopLevelDefaults()
    {
        var result = WorkflowDefinitionParser.Parse("""
            defaults: {}
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("defaults", error.Path);
        Assert.Equal("unknown field 'defaults'", error.Message);
    }

    [Fact]
    public void Parse_Rejects_TopLevelArtifacts()
    {
        var result = WorkflowDefinitionParser.Parse("""
            artifacts:
              files:
                - path: out.md
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("artifacts", error.Path);
        Assert.Equal("unknown field 'artifacts'", error.Message);
    }

    [Fact]
    public void Parse_EmptyStages_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("stages", error.Path);
        Assert.Equal("stages must be non-empty", error.Message);
    }

    [Fact]
    public void Parse_Definition_DoesNotPersistRemovedTopLevelFields()
    {
        var result = WorkflowDefinitionParser.Parse("""
            id: stripped
            variables: {}
            defaults: {}
            artifacts: {}
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Definition);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result.Definition!);
        Assert.DoesNotContain("id", serialized);
        Assert.DoesNotContain("variables", serialized);
        Assert.DoesNotContain("defaults", serialized);
        Assert.DoesNotContain("artifacts", serialized);
    }

    [Fact]
    public void Parse_MissingStages_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - id: apply
                    uses: mohist/opencode
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("stages", error.Path);
        Assert.Equal("stages is required", error.Message);
    }

    [Fact]
    public void Parse_RootNotObject_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("""
            - stage: build
              tasks: []
              checks: []
            """);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("", error.Path);
        Assert.Equal("definition root must be an object", error.Message);
    }
}
