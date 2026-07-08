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
    public void Render_ResolvedStringSubstitutesValueAndLeavesMissingListEmpty()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, _) = engine.Render(
            "Hello ${{ issue.number }}",
            Variables("{ \"issue\": { \"number\": 42 } }"));

        Assert.Equal("Hello 42", rendered);
        Assert.Empty(missing);
    }

    [Fact]
    public void Render_NestedPathResolvesThroughObjects()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, _) = engine.Render(
            "Owner: ${{ project.name }}",
            Variables("{ \"project\": { \"name\": \"Mohist\" } }"));

        Assert.Equal("Owner: Mohist", rendered);
        Assert.Empty(missing);
    }

    [Fact]
    public void Render_MissingVariableIsLeftInPlaceAndRecorded()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, depth) = engine.Render(
            "Hello ${{ issue.priority }}",
            Variables("{ \"issue\": { \"number\": 42 } }"));

        Assert.Contains("${{ issue.priority }}", rendered);
        Assert.Contains("issue.priority", missing);
        Assert.DoesNotContain("issue.number", missing);
        Assert.Equal(0, depth);
    }

    [Fact]
    public void Render_ChainedStringExpansionConvergesWithinFivePasses()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, depth) = engine.Render(
            "${{ a }}",
            Variables("{ \"a\": \"${{ b }}\", \"b\": \"${{ c }}\", \"c\": \"value\" }"));

        Assert.Equal("value", rendered);
        Assert.Empty(missing);
        Assert.True(
            depth <= PromptTemplateEngine.MaxPasses,
            $"Convergence must happen within {PromptTemplateEngine.MaxPasses} passes, but engine ran {depth}.");
        Assert.Equal(3, depth);
    }

    [Fact]
    public void Render_NonConvergingCyclicInputIsBoundedByMaxPasses()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, depth) = engine.Render(
            "${{ a }}",
            Variables("{ \"a\": \"${{ b }}\", \"b\": \"${{ a }}\" }"));

        Assert.Equal(PromptTemplateEngine.MaxPasses, depth);
        Assert.NotEmpty(missing);
        Assert.Contains("${{", rendered);
    }

    [Fact]
    public void Render_SelfReferentialTokenDoesNotLoopAndIsReportedAsMissing()
    {
        var engine = new PromptTemplateEngine();

        var (rendered, missing, depth) = engine.Render(
            "${{ a }}",
            Variables("{ \"a\": \"${{ a }}\" }"));

        Assert.Equal("${{ a }}", rendered);
        Assert.Contains("a", missing);
        Assert.Equal(0, depth);
    }

    [Fact]
    public void Render_ObjectArrayNumberAndBooleanAreJsonStringified()
    {
        var engine = new PromptTemplateEngine();

        var variables = Variables(
            "{ \"data\": { \"obj\": { \"x\": 1 }, \"arr\": [1, 2], \"n\": 42, \"flag\": true } }");

        var (objectRendered, objectMissing, _) = engine.Render("${{ data.obj }}", variables);
        var (arrayRendered, arrayMissing, _) = engine.Render("${{ data.arr }}", variables);
        var (numberRendered, numberMissing, _) = engine.Render("${{ data.n }}", variables);
        var (boolRendered, boolMissing, _) = engine.Render("${{ data.flag }}", variables);

        Assert.Equal("{ \"x\": 1 }", objectRendered);
        Assert.Empty(objectMissing);
        Assert.Equal("[1, 2]", arrayRendered);
        Assert.Empty(arrayMissing);
        Assert.Equal("42", numberRendered);
        Assert.Empty(numberMissing);
        Assert.Equal("true", boolRendered);
        Assert.Empty(boolMissing);
    }

    [Fact]
    public void Render_NullValueResolvesToLiteralStringNull()
    {
        var engine = new PromptTemplateEngine();

        var variables = Variables("{ \"data\": { \"nil\": null } }");

        var (rendered, missing, _) = engine.Render("${{ data.nil }}", variables);

        Assert.Equal("null", rendered);
        Assert.Empty(missing);
    }

    [Fact]
    public void ExtractVariables_ReturnsSortedDeduplicatedPathsWithoutRendering()
    {
        var body = "Use ${{ openspecChangeDir }} and ${{ issue.number }} and ${{ openspecChangeDir }}";

        var variables = PromptTemplateEngine.ExtractVariables(body);

        Assert.Equal(new[] { "issue.number", "openspecChangeDir" }, variables.ToArray());
    }

    [Fact]
    public void ExtractVariables_DoesNotRequireVariablesToBeResolvable()
    {
        var body = "${{ does.not.exist }} nested with ${{ another.missing }}";

        var variables = PromptTemplateEngine.ExtractVariables(body);

        Assert.Equal(new[] { "another.missing", "does.not.exist" }, variables.ToArray());
    }
}
