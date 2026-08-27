using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Mohist.Workflow.Definition;

public static class WorkflowProfileParser
{
    public static WorkflowProfileParseResult Parse(
        string yaml,
        string fallbackId)
    {
        YamlStream stream;
        try
        {
            using var reader = new StringReader(yaml);
            stream = new YamlStream();
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            return Invalid(new ValidationError("", $"invalid YAML: {exception.Message}"));
        }

        if (stream.Documents.Count > 1)
            return Invalid(new ValidationError("", "yaml must contain exactly one document"));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return Invalid(new ValidationError("", "definition root must be an object"));

        var id = Scalar(root, "id") ?? fallbackId;
        var name = Scalar(root, "name") ?? id;
        var description = Scalar(root, "description") ?? string.Empty;
        Remove(root, "id");
        Remove(root, "name");
        Remove(root, "description");

        var definitionResult = WorkflowDefinitionParser.Parse(Serialize(root));
        var profile = definitionResult.Definition is null
            ? null
            : new WorkflowProfile(id, name, description, definitionResult.Definition);
        return new WorkflowProfileParseResult(profile, WorkflowDefinitionValidator.Sort(definitionResult.Errors.ToList()));
    }

    private static WorkflowProfileParseResult Invalid(params ValidationError[] errors) =>
        new(null, errors);

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
