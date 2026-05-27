using System.Text.Json;
using System.Text.Json.Nodes;
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

    public string ToDispatchJson(WorkflowDispatchContext dispatch)
    {
        var payload = ParseObject(Json);

        // Merge stage-level variable overrides with deep merge support
        if (StageVariables is not null
            && !string.IsNullOrWhiteSpace(dispatch.Stage)
            && StageVariables.TryGetValue(dispatch.Stage, out var stageOverrides))
        {
            foreach (var (section, value) in stageOverrides)
            {
                if (payload.TryGetValue(section, out var existing) && existing.HasValue)
                {
                    // Deep merge: try to parse both as objects and merge
                    var merged = DeepMerge(existing.Value, value);
                    if (merged is not null)
                    {
                        payload[section] = merged;
                        continue;
                    }
                }
                // Fallback: full replacement
                payload[section] = JsonSerializer.SerializeToElement(value);
            }
        }

        payload["workflow"] = JsonSerializer.SerializeToElement(new { runId = dispatch.WorkflowRunId }, WorkflowVariableJson.Options);
        payload["stage"] = JsonSerializer.SerializeToElement(new { name = dispatch.Stage }, WorkflowVariableJson.Options);
        payload["work"] = JsonSerializer.SerializeToElement(new { id = dispatch.WorkId, type = dispatch.WorkType, title = dispatch.Title, attempt = dispatch.Attempt }, WorkflowVariableJson.Options);

        return JsonSerializer.Serialize(payload, WorkflowVariableJson.Options);
    }

    /// <summary>
    /// Deep merge a JSON element with a JSON string. Returns null if merge is not possible.
    /// </summary>
    private static JsonElement? DeepMerge(JsonElement existing, string overrideJson)
    {
        try
        {
            if (existing.ValueKind != JsonValueKind.Object)
                return null;

            var overrideObj = JsonSerializer.Deserialize<JsonElement>(overrideJson);
            if (overrideObj.ValueKind != JsonValueKind.Object)
                return null;

            var merged = JsonNode.Parse(existing.GetRawText())?.AsObject();
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

    private static JsonNode? MergeNode(JsonNode? existing, JsonElement patch)
    {
        if (existing is JsonObject existingObject && patch.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in patch.EnumerateObject())
                existingObject[property.Name] = MergeNode(existingObject[property.Name], property.Value);
            return existingObject;
        }

        return JsonNode.Parse(patch.GetRawText());
    }

    private static JsonElement MergeSection(JsonElement? existing, string patchJson)
    {
        if (existing.HasValue)
        {
            var merged = DeepMerge(existing.Value, patchJson);
            if (merged is not null) return merged.Value;
        }

        return JsonSerializer.Deserialize<JsonElement>(patchJson).Clone();
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
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(section, out var sectionValue)
            && sectionValue.ValueKind == JsonValueKind.Object
            && sectionValue.TryGetProperty(property, out var propertyValue)
            ? propertyValue.GetString()
            : null;
    }

    private static Dictionary<string, JsonElement?> ParseObject(string json)
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

[GenerateSerializer]
public sealed record WorkflowDispatchContext(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string WorkId,
    [property: Id(2)] string WorkType,
    [property: Id(3)] string? Stage,
    [property: Id(4)] string? Title,
    [property: Id(5)] int Attempt);

public static class WorkflowVariableJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
