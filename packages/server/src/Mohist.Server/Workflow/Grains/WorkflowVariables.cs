using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Grains;

[GenerateSerializer]
public sealed record WorkflowExecutionContext(
    [property: Id(0)] string Json,
    [property: Id(1)] Dictionary<string, Dictionary<string, string>>? StageVariables = null)
{
    public WorkflowExecutionContext PatchSection(string section, string patchJson)
    {
        var payload = ParseObject(Json);
        payload[section] = MergeSection(payload.TryGetValue(section, out var existing) ? existing : null, patchJson);
        return new WorkflowExecutionContext(JsonSerializer.Serialize(payload, WorkflowVariableJson.Options), CopyStageVariables(StageVariables));
    }

    public WorkflowExecutionContext PatchStageSection(string stage, string section, string patchJson)
    {
        var stageVariables = CopyStageVariables(StageVariables) ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!stageVariables.TryGetValue(stage, out var sections))
        {
            sections = new Dictionary<string, string>(StringComparer.Ordinal);
            stageVariables[stage] = sections;
        }

        sections[section] = sections.TryGetValue(section, out var existing)
            ? MergeJsonStrings(existing, patchJson)
            : NormalizeJsonString(patchJson);

        return new WorkflowExecutionContext(Json, stageVariables);
    }

    public static JsonElement MergeSection(JsonElement? existing, string patchJson)
    {
        if (existing.HasValue)
        {
            var merged = DeepMerge(existing.Value, patchJson);
            if (merged is not null) return merged.Value;
        }

        return JsonSerializer.Deserialize<JsonElement>(patchJson).Clone();
    }

    private static JsonElement? DeepMerge(JsonElement existing, string overrideJson)
    {
        try
        {
            if (existing.ValueKind != JsonValueKind.Object)
                return null;

            var overrideObj = JsonSerializer.Deserialize<JsonElement>(overrideJson);
            if (overrideObj.ValueKind != JsonValueKind.Object)
                return null;

            var merged = System.Text.Json.Nodes.JsonNode.Parse(existing.GetRawText())?.AsObject();
            if (merged is null)
                return null;

            foreach (var property in overrideObj.EnumerateObject())
                merged[property.Name] = MergeNode(merged[property.Name], property.Value);

            return JsonSerializer.Deserialize<JsonElement>(merged.ToJsonString());
        }
        catch
        {
            return null;
        }
    }

    private static System.Text.Json.Nodes.JsonNode? MergeNode(System.Text.Json.Nodes.JsonNode? existing, JsonElement patch)
    {
        if (existing is System.Text.Json.Nodes.JsonObject existingObject && patch.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in patch.EnumerateObject())
                existingObject[property.Name] = MergeNode(existingObject[property.Name], property.Value);
            return existingObject;
        }

        return System.Text.Json.Nodes.JsonNode.Parse(patch.GetRawText());
    }

    private static string MergeJsonStrings(string existingJson, string patchJson)
    {
        try
        {
            var existing = JsonSerializer.Deserialize<JsonElement>(existingJson);
            var merged = DeepMerge(existing, patchJson);
            return merged is not null
                ? merged.Value.GetRawText()
                : NormalizeJsonString(patchJson);
        }
        catch
        {
            return NormalizeJsonString(patchJson);
        }
    }

    private static string NormalizeJsonString(string json) => JsonSerializer.Deserialize<JsonElement>(json).GetRawText();

    private static Dictionary<string, Dictionary<string, string>>? CopyStageVariables(Dictionary<string, Dictionary<string, string>>? source)
    {
        if (source is null) return null;
        return source.ToDictionary(
            stage => stage.Key,
            stage => new Dictionary<string, string>(stage.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    public string? String(string section, string property)
    {
        using var document = JsonDocument.Parse(Json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(section, out var sectionValue)
            || sectionValue.ValueKind != JsonValueKind.Object
            || !sectionValue.TryGetProperty(property, out var propertyValue))
            return null;

        return propertyValue.ValueKind switch
        {
            JsonValueKind.String => propertyValue.GetString(),
            JsonValueKind.Number => propertyValue.GetRawText(),
            _ => propertyValue.GetRawText(),
        };
    }

    public static Dictionary<string, JsonElement?> ParseObject(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement?>(StringComparer.Ordinal);

        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (JsonElement?)property.Value.Clone(),
            StringComparer.Ordinal);
    }
}

public static class WorkflowVariableJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
