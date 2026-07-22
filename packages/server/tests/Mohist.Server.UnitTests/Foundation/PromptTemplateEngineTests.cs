using System.Text.Json;
using Mohist.Server.Workflow.Services.Prompts;
using Xunit;

namespace Mohist.Server.UnitTests.Foundation;

public class PromptTemplateEngineTests
{
    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Render_CompleteAndEmbeddedScalarsPreservePromptText()
    {
        var result = new PromptTemplateEngine().Render(
            "issue-${{ issue.number }} / ${{ vars.flag }} / ${{ vars.nil }}",
            Variables("{ \"issue\": { \"number\": 42 }, \"vars\": { \"flag\": true, \"nil\": null } }"));

        Assert.Equal("issue-42 / true / ", result.Rendered);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Render_MissingWholeAndEmbeddedReferencesReportErrorsAndDoNotRemain()
    {
        var result = new PromptTemplateEngine().Render(
            "before ${{ vars.missing }} after ${{ tasks.unknown.outputs.result }}",
            Variables("{}"));

        Assert.DoesNotContain("${{", result.Rendered);
        Assert.Equal(new[] { "tasks.unknown.outputs.result", "vars.missing" }, result.MissingVariables);
        Assert.All(result.Errors, error => Assert.Equal("missing_reference", error.Code));
    }

    [Fact]
    public void Render_ObjectsAndArraysAreRejectedInCompleteAndEmbeddedExpressions()
    {
        var variables = Variables("{ \"vars\": { \"object\": { \"x\": 1 }, \"array\": [1, 2] } }");

        var complete = new PromptTemplateEngine().Render("${{ vars.object }}", variables);
        var embedded = new PromptTemplateEngine().Render("value=${{ vars.array }}", variables);

        Assert.Contains(complete.Errors, error => error.Code == "invalid_type" && error.Path == "vars.object");
        Assert.Contains(embedded.Errors, error => error.Code == "invalid_type" && error.Path == "vars.array");
        Assert.DoesNotContain("${{", complete.Rendered);
        Assert.DoesNotContain("${{", embedded.Rendered);
    }

    [Fact]
    public void Render_EscapedReferenceRemainsLiteral()
    {
        var result = new PromptTemplateEngine().Render(
            @"use \${{ vars.foo }}",
            Variables("{ \"vars\": { \"foo\": \"expanded\" } }"));

        Assert.Equal("use ${{ vars.foo }}", result.Rendered);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Render_ChainedStringExpansionConverges()
    {
        var result = new PromptTemplateEngine().Render(
            "${{ vars.a }}",
            Variables("{ \"vars\": { \"a\": \"${{ vars.b }}\", \"b\": \"${{ vars.c }}\", \"c\": \"value\" } }"));

        Assert.Equal("value", result.Rendered);
        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Depth);
    }

    [Fact]
    public void Render_CyclesAndOverDepthFailDeterministically()
    {
        var cycle = new PromptTemplateEngine().Render(
            "${{ vars.a }}",
            Variables("{ \"vars\": { \"a\": \"${{ vars.b }}\", \"b\": \"${{ vars.a }}\" } }"));
        var deep = new PromptTemplateEngine().Render(
            "${{ vars.a }}",
            Variables("{ \"vars\": { \"a\": \"${{ vars.b }}\", \"b\": \"${{ vars.c }}\", \"c\": \"${{ vars.d }}\", \"d\": \"${{ vars.e }}\", \"e\": \"${{ vars.f }}\", \"f\": \"done\" } }"));

        Assert.Contains(cycle.Errors, error => error.Code == "cycle");
        Assert.Contains(deep.Errors, error => error.Code == "max_depth");
        Assert.DoesNotContain("${{", cycle.Rendered);
        Assert.DoesNotContain("${{", deep.Rendered);
    }

    [Fact]
    public void ExtractVariables_ReturnsSortedDeduplicatedPathsWithoutRendering()
    {
        var variables = PromptTemplateEngine.ExtractVariables(
            "Use ${{ vars.foo }} and ${{ issue.number }} and ${{ vars.foo }}");

        Assert.Equal(new[] { "issue.number", "vars.foo" }, variables.ToArray());
    }

    [Fact]
    public void ExtractVariables_IgnoresEscapedReferences()
    {
        var variables = PromptTemplateEngine.ExtractVariables(
            @"Use \${{ vars.literal }} and ${{ vars.actual }}");

        Assert.Equal(new[] { "vars.actual" }, variables.ToArray());
    }

    [Fact]
    public void ExtractVariables_CanValidateReferencesWithTheSameRenderer()
    {
        var body = "see ${{ vars.missing }} and ${{ vars.object }}";
        var result = new PromptTemplateEngine().Render(body, Variables("{ \"vars\": { \"object\": {} } }"));

        Assert.Contains(result.Errors, error => error.Code == "missing_reference" && error.Path == "vars.missing");
        Assert.Contains(result.Errors, error => error.Code == "invalid_type" && error.Path == "vars.object");
    }

    [Fact]
    public void Render_RejectsBareVariablesAndOffTableRoots()
    {
        var result = new PromptTemplateEngine().Render(
            "${{ foo }} ${{ project.id }} ${{ vars.foo }}",
            Variables("{ \"foo\": \"bare\", \"project\": { \"id\": \"project-1\" }, \"vars\": { \"foo\": \"namespaced\" } }"));

        Assert.Equal("  namespaced", result.Rendered);
        Assert.Contains(result.Errors, error => error.Code == "missing_reference" && error.Path == "foo");
        Assert.Contains(result.Errors, error => error.Code == "missing_reference" && error.Path == "project.id");
    }
}
