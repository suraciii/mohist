using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public class ParseEntryTests
{
    [Fact]
    public void Parse_ReturnsDefinition_OnMinimalValidYaml()
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
        Assert.NotNull(result.Definition);
        Assert.Empty(result.Errors);
        var definition = result.Definition!;
        Assert.Single(definition.Stages);
        Assert.Equal("build", definition.Stages[0].Stage);
        var task = Assert.Single(definition.Stages[0].Tasks);
        Assert.Equal("t1", task.Id);
        Assert.Equal("mohist/opencode", task.Uses);
        Assert.Null(task.Title);
        Assert.Null(definition.Approval);
    }

    [Fact]
    public void Parse_NullInput_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse(null!);

        Assert.Null(result.Definition);
        var error = Assert.Single(result.Errors);
        Assert.Equal("", error.Path);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    [Fact]
    public void Parse_EmptyYaml_ReturnsValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("");

        Assert.Null(result.Definition);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_InvalidYamlSyntax_ReturnsSingleValidationError()
    {
        var result = WorkflowDefinitionParser.Parse("stages: [unclosed");

        Assert.Null(result.Definition);
        var error = Assert.Single(result.Errors);
        Assert.Equal("", error.Path);
        Assert.Contains("yaml syntax error", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_Errors_AreSortedByPath()
    {
        var result = WorkflowDefinitionParser.Parse("""
            approval:
              feedback:
                tasks:
                  - uses: foo
            stages:
              - stage: build
                tasks:
                  - id: same
                    uses: foo
                  - id: same
                    uses: foo
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        var paths = result.Errors.Select(e => e.Path).ToArray();
        Assert.Equal(paths, paths.OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Parse_Errors_NeverExposeStackTraceOrTypeNames()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks:
                  - id: t1
                checks: []
            """);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error =>
        {
            Assert.DoesNotContain("at ", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkflowDefinition", error.Message);
            Assert.DoesNotContain("YamlDotNet", error.Message);
            Assert.DoesNotContain(".cs:", error.Message);
            Assert.DoesNotContain("in ", error.Message);
        });
    }

    [Fact]
    public void Parse_Result_DefinitionCarriesNullApproval_WhenAbsent()
    {
        var result = WorkflowDefinitionParser.Parse("""
            stages:
              - stage: build
                tasks: []
                checks: []
            """);

        Assert.True(result.IsValid);
        Assert.Null(result.Definition!.Approval);
    }
}
