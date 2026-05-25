using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Grains;

[GenerateSerializer]
public sealed record WorkflowExecutionContext(
    [property: Id(0)] string Json)
{
    public string ToDispatchJson(WorkflowDispatchContext dispatch)
    {
        var payload = ParseObject(Json);

        payload["workflow"] = JsonSerializer.SerializeToElement(new { runId = dispatch.WorkflowRunId }, WorkflowVariableJson.Options);
        payload["stage"] = JsonSerializer.SerializeToElement(new { name = dispatch.Stage }, WorkflowVariableJson.Options);
        payload["work"] = JsonSerializer.SerializeToElement(new { id = dispatch.WorkId, type = dispatch.WorkType, title = dispatch.Title, attempt = dispatch.Attempt }, WorkflowVariableJson.Options);

        return JsonSerializer.Serialize(payload, WorkflowVariableJson.Options);
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
