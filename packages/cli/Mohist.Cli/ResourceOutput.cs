using System.Text.Json.Nodes;

namespace Mohist.Cli;

internal enum ResourceCardinality
{
    Single,
    Collection,
    Stream,
}

internal sealed record ResourceDescriptor(
    ResourceCardinality Cardinality,
    IReadOnlyList<string> Fields);

internal static class ResourceOutputCatalog
{
    private static readonly IReadOnlyList<string> CommonFields =
        ["id", "number", "name", "title", "description", "status", "state", "stage", "priority", "labels", "createdAt", "updatedAt"];

    public static ResourceDescriptor For(string? tableShape)
    {
        if (!Enum.TryParse<MohistCliApi.TableShape>(tableShape, ignoreCase: false, out var shape))
            return new(ResourceCardinality.Single, CommonFields);

        var cardinality = shape switch
        {
            MohistCliApi.TableShape.ProjectList or
            MohistCliApi.TableShape.IssueList or
            MohistCliApi.TableShape.RepoList or
            MohistCliApi.TableShape.FeedbackList or
            MohistCliApi.TableShape.AgentList or
            MohistCliApi.TableShape.EpicList or
            MohistCliApi.TableShape.Sessions or
            MohistCliApi.TableShape.RunnerList or
            MohistCliApi.TableShape.WorkflowRunEvents or
            MohistCliApi.TableShape.WorkflowRunVariables or
            MohistCliApi.TableShape.WorkflowVariables or
            MohistCliApi.TableShape.ProjectTemplateList or
            MohistCliApi.TableShape.IssueTemplateList or
            MohistCliApi.TableShape.RoutingRuleList or
            MohistCliApi.TableShape.DeadLetterList or
            MohistCliApi.TableShape.OpencodeModels => ResourceCardinality.Collection,
            _ => ResourceCardinality.Single,
        };

        var fields = shape switch
        {
            MohistCliApi.TableShape.WorkflowRunEvents => ["id", "type", "source", "subject", "time", "data"],
            MohistCliApi.TableShape.WorkflowVariables => ["vars", "stages"],
            MohistCliApi.TableShape.WorkflowProfile => ["id", "displayName", "description", "enabled", "defaultTemplate", "variables", "prompts"],
            MohistCliApi.TableShape.RoutingRule or MohistCliApi.TableShape.RoutingRuleList => ["id", "name", "target", "priority", "enabled", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.DeadLetterList or MohistCliApi.TableShape.DeadLetterRedelivery => ["id", "eventId", "handler", "attempts", "status", "createdAt", "updatedAt"],
            MohistCliApi.TableShape.OpencodeModels => ["id", "name", "provider"],
            MohistCliApi.TableShape.RunnerShow => ["id", "kind", "hostname", "scope", "capabilities", "coderModels", "capacity", "status", "connectionState", "lastHeartbeatAt"],
            MohistCliApi.TableShape.SystemInfo => ["running", "source", "install", "update", "services", "paths"],
            _ => CommonFields,
        };

        return new(cardinality, fields);
    }
}

internal enum JsonSelectionKind
{
    None,
    Discovery,
    Selected,
    Invalid,
}

internal sealed record JsonSelection(JsonSelectionKind Kind, IReadOnlyList<string> Fields, string? InvalidField)
{
    public static JsonSelection Parse(ResourceDescriptor descriptor, bool provided, string? value)
    {
        if (!provided)
            return new(JsonSelectionKind.None, [], null);
        if (value is null)
            return new(JsonSelectionKind.Discovery, descriptor.Fields, null);

        var fields = value.Split(',', StringSplitOptions.None);
        var selected = new List<string>(fields.Length);
        foreach (var raw in fields)
        {
            var field = raw.Trim();
            if (field.Length == 0 || !descriptor.Fields.Contains(field, StringComparer.Ordinal))
                return new(JsonSelectionKind.Invalid, [], field.Length == 0 ? raw : field);
            if (selected.Contains(field, StringComparer.Ordinal))
                return new(JsonSelectionKind.Invalid, [], field);
            selected.Add(field);
        }

        return new(JsonSelectionKind.Selected, selected, null);
    }

    public JsonNode Project(JsonNode? data, ResourceCardinality cardinality)
    {
        return cardinality switch
        {
            ResourceCardinality.Single => ProjectObject(data as JsonObject),
            ResourceCardinality.Collection => ProjectCollection(data as JsonArray),
            ResourceCardinality.Stream => ProjectObject(data as JsonObject),
            _ => throw new InvalidOperationException("Unknown resource cardinality"),
        };
    }

    private JsonObject ProjectObject(JsonObject? source)
    {
        if (source is null)
            throw new InvalidOperationException("The server returned a non-object resource");
        var result = new JsonObject();
        foreach (var field in Fields)
            result[field] = source[field]?.DeepClone();
        return result;
    }

    private JsonArray ProjectCollection(JsonArray? source)
    {
        if (source is null)
            throw new InvalidOperationException("The server returned a non-array collection");
        var result = new JsonArray();
        foreach (var item in source)
            result.Add(ProjectObject(item as JsonObject));
        return result;
    }
}
