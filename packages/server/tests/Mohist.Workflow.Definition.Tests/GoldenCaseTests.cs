using System.Reflection;
using Mohist.Workflow.Definition;
using Xunit;

namespace Mohist.Workflow.Definition.Tests;

public sealed class GoldenCaseTests
{
    private static readonly Assembly TestAssembly = typeof(GoldenCaseTests).Assembly;

    [Fact]
    public void EveryBuiltInWorkflowDefinition_ParsesSuccessfully()
    {
        var resources = TestAssembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("GoldenCases.mohist-", StringComparison.Ordinal)
                && name.EndsWith(".workflow.yaml", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(resources);

        foreach (var resource in resources)
        {
            var result = WorkflowDefinitionParser.Parse(ReadResource(resource));

            Assert.True(result.IsValid, FormatErrors(resource, result.Errors));
        }
    }

    [Fact]
    public void CompleteDocumentationExample_ParsesSuccessfully()
    {
        var example = ExtractCompleteDocumentationExample(ReadResource("GoldenCases.workflow-definition.md"));
        var result = WorkflowDefinitionParser.Parse(example);

        Assert.True(result.IsValid, FormatErrors("docs/workflow-definition.md", result.Errors));
    }

    [Fact]
    public void DocumentationExampleExtractor_SelectsOnlyTheCompleteExampleFence()
    {
        var markdown = ReadResource("GoldenCases.workflow-definition.md");
        var selected = ExtractCompleteDocumentationExample(markdown);
        var allYamlFences = ExtractYamlFences(markdown).ToArray();

        Assert.Single(allYamlFences, fence => fence == selected);
        Assert.DoesNotContain("<Task>", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("<Stage>", selected, StringComparison.Ordinal);
        Assert.DoesNotContain("<Expect>", selected, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFieldInjectedIntoGoldenCase_UsesCanonicalValidationError()
    {
        var example = ExtractCompleteDocumentationExample(ReadResource("GoldenCases.workflow-definition.md"));
        var injected = example.Replace("\nstages:\n", "\nunknownGoldenField: true\nstages:\n", StringComparison.Ordinal);

        var result = WorkflowDefinitionParser.Parse(injected);

        var error = Assert.Single(result.Errors, item => item.Path == "unknownGoldenField");
        Assert.Equal("unknownGoldenField", error.Path);
        Assert.Equal("unknown field 'unknownGoldenField'", error.Message);
        Assert.Equal(ValidationSource.Definition, error.Source);
    }

    private static string ReadResource(string resourceName)
    {
        using var stream = TestAssembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string ExtractCompleteDocumentationExample(string markdown)
    {
        const string heading = "## 完整示例";
        var headingIndex = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(headingIndex >= 0, "The complete example heading is missing.");

        var section = markdown[headingIndex..];
        var fenceStart = section.IndexOf("```yaml\n", StringComparison.Ordinal);
        Assert.True(fenceStart >= 0, "The complete example YAML fence is missing.");
        fenceStart += "```yaml\n".Length;

        var fenceEnd = section.IndexOf("\n```", fenceStart, StringComparison.Ordinal);
        Assert.True(fenceEnd >= 0, "The complete example YAML fence is not closed.");

        return section[fenceStart..fenceEnd].Trim() + "\n";
    }

    private static IEnumerable<string> ExtractYamlFences(string markdown)
    {
        const string openingFence = "```yaml\n";
        var offset = 0;

        while (true)
        {
            var start = markdown.IndexOf(openingFence, offset, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            start += openingFence.Length;
            var end = markdown.IndexOf("\n```", start, StringComparison.Ordinal);
            Assert.True(end >= 0, "A YAML fence is not closed.");
            yield return markdown[start..end].Trim() + "\n";
            offset = end + 4;
        }
    }

    private static string FormatErrors(string resource, IReadOnlyList<ValidationError> errors)
    {
        return $"{resource}: {string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"))}";
    }
}
