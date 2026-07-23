using System.Text;
using Mohist.Server.Runner.Grains;
using Mohist.Workflow.Definition;
using YamlDotNet.RepresentationModel;

namespace Mohist.Server.Workflow.Services;

internal static class WorkflowProfileYamlParser
{
    public static WorkflowProfile Parse(string yaml, string fallbackId, ActionCatalog? catalog = null)
    {
        YamlStream stream;
        using (var reader = new StringReader(yaml))
        {
            stream = new YamlStream();
            stream.Load(reader);
        }

        if (stream.Documents.Count > 1)
            throw new WorkflowDefinitionValidationException(
                [new ValidationError("", "yaml must contain exactly one document")]);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new WorkflowDefinitionValidationException([new ValidationError("", "definition root must be an object")]);

        var id = Scalar(root, "id") ?? fallbackId;
        var name = Scalar(root, "name") ?? id;
        var description = Scalar(root, "description") ?? string.Empty;

        Remove(root, "id");
        Remove(root, "name");
        Remove(root, "description");

        var definitionYaml = Serialize(root);
        var result = WorkflowDefinitionParser.Parse(definitionYaml);
        var errors = result.Errors.ToList();
        if (result.Definition is not null && catalog is not null)
            errors.AddRange(ActionContractValidator.Validate(result.Definition, catalog));
        if (errors.Count > 0)
            throw new WorkflowDefinitionValidationException(errors
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(error => error.Message, StringComparer.Ordinal)
                .ToArray());

        return new WorkflowProfile(id, name, description, result.Definition!);
    }

    private static string? Scalar(YamlMappingNode root, string key) =>
        root.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode { Value: var value } && value == key).Value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static void Remove(YamlMappingNode root, string key)
    {
        var entry = root.Children.Keys.FirstOrDefault(node => node is YamlScalarNode { Value: var value } && value == key);
        if (entry is not null)
            root.Children.Remove(entry);
    }

    private static string Serialize(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
