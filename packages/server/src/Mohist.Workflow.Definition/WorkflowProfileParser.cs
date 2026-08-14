using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Mohist.Workflow.Definition;

public static class WorkflowProfileParser
{
    public const string AgentActionExpression = "${{ profile.agentAction }}";

    public static WorkflowProfileParseResult Parse(
        string yaml,
        string fallbackId,
        string? agentActionOverride = null)
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
        var errors = new List<ValidationError>();
        var sourceAgentAction = ReadAgentAction(root, errors);
        var effectiveAgentAction = NormalizeAgentAction(agentActionOverride) ?? sourceAgentAction;
        if (agentActionOverride is not null && sourceAgentAction is null)
            errors.Add(new ValidationError("agentAction", "Profile does not declare an Agent Action binding"));

        Remove(root, "id");
        Remove(root, "name");
        Remove(root, "description");
        Remove(root, "agentAction");

        var referenceCount = MaterializeAgentAction(root, effectiveAgentAction, errors);
        if (sourceAgentAction is not null && referenceCount == 0)
        {
            errors.Add(new ValidationError(
                "agentAction",
                $"agentAction requires at least one complete 'uses: {AgentActionExpression}' reference"));
        }

        var definitionResult = WorkflowDefinitionParser.Parse(Serialize(root));
        errors.AddRange(definitionResult.Errors);
        var profile = definitionResult.Definition is null
            ? null
            : new WorkflowProfile(id, name, description, definitionResult.Definition, effectiveAgentAction);
        return new WorkflowProfileParseResult(profile, WorkflowDefinitionValidator.Sort(errors));
    }

    private static WorkflowProfileParseResult Invalid(params ValidationError[] errors) =>
        new(null, errors);

    private static string? ReadAgentAction(YamlMappingNode root, List<ValidationError> errors)
    {
        var entry = root.Children.FirstOrDefault(pair =>
            pair.Key is YamlScalarNode { Value: "agentAction" });
        if (entry.Key is null)
            return null;

        if (entry.Value is not YamlScalarNode scalar)
        {
            errors.Add(new ValidationError("agentAction", "agentAction must be a concrete Action name"));
            return null;
        }

        var value = NormalizeAgentAction(scalar.Value);
        if (value is null || value.Contains("${{", StringComparison.Ordinal))
        {
            errors.Add(new ValidationError("agentAction", "agentAction must be a non-empty concrete Action name"));
            return null;
        }
        return value;
    }

    private static string? NormalizeAgentAction(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int MaterializeAgentAction(
        YamlNode node,
        string? agentAction,
        List<ValidationError> errors,
        string path = "")
    {
        var count = 0;
        if (node is YamlMappingNode mapping)
        {
            foreach (var pair in mapping.Children.ToArray())
            {
                var key = (pair.Key as YamlScalarNode)?.Value ?? string.Empty;
                var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
                if (pair.Value is YamlScalarNode scalar
                    && scalar.Value?.Contains(AgentActionExpression, StringComparison.Ordinal) == true)
                {
                    if (!string.Equals(key, "uses", StringComparison.Ordinal)
                        || !string.Equals(scalar.Value, AgentActionExpression, StringComparison.Ordinal))
                    {
                        errors.Add(new ValidationError(
                            childPath,
                            $"{AgentActionExpression} is valid only as the complete value of uses"));
                        continue;
                    }

                    if (agentAction is null)
                    {
                        errors.Add(new ValidationError(
                            childPath,
                            $"{AgentActionExpression} requires a non-empty agentAction"));
                        continue;
                    }

                    mapping.Children[pair.Key] = new YamlScalarNode(agentAction)
                    {
                        Style = scalar.Style,
                    };
                    count++;
                    continue;
                }

                count += MaterializeAgentAction(pair.Value, agentAction, errors, childPath);
            }
            return count;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                var childPath = $"{path}[{index}]";
                count += MaterializeAgentAction(sequence.Children[index], agentAction, errors, childPath);
            }
        }
        return count;
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
