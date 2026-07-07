using System.Text.Json;
using Orleans;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Workflow.Domain;

/// <summary>
/// Variables shared by project, issue, and workflow-run scopes.
/// </summary>
[GenerateSerializer]
public sealed record VariableBundle(
    [property: Id(0)] JsonElement? Vars = null,
    [property: Id(1)] Dictionary<string, StageVariables>? Stages = null)
{
    public static readonly VariableBundle Empty = new();

    public static VariableBundle FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<VariableBundle>(json, JsonOptions);
            return parsed ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static VariableBundle FromElement(JsonElement? element)
    {
        if (!element.HasValue)
            return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<VariableBundle>(element.Value.GetRawText(), JsonOptions);
            return parsed ?? Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public JsonElement ToElement() =>
        JSON.DeserializeElement(ToJson());

    public static VariableBundle Set(VariableBundle bundle) => bundle;

    public static VariableBundle Patch(VariableBundle? @base, VariableBundle? overlay)
    {
        if (overlay is null) return @base ?? Empty;
        @base ??= Empty;

        var mergedVars = VariableJsonMerge.ApplyPatch(@base.Vars, overlay.Vars);
        var mergedStages = MergeStages(@base.Stages, overlay.Stages);

        return new VariableBundle(
            mergedVars,
            mergedStages is { Count: > 0 } ? mergedStages : null);
    }

    public static VariableBundle MergeAll(params VariableBundle?[] layers)
    {
        var result = Empty;
        foreach (var layer in layers)
            result = Patch(result, layer);
        return result;
    }

    public JsonElement? ResolveStageVars(string? stage)
    {
        if (!Vars.HasValue && Stages is null) return null;

        JsonElement? effective = Vars.HasValue && Vars.Value.ValueKind == JsonValueKind.Object
            ? Vars.Value
            : JSON.DeserializeElement("{}");

        if (Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            effective = VariableJsonMerge.ApplyPatch(effective, stageVars.Vars.Value) ?? stageVars.Vars.Value.Clone();
        }

        return effective;
    }

    public static JsonElement GetByKeyPath(JsonElement? root, string? keyPath)
    {
        if (!root.HasValue || string.IsNullOrWhiteSpace(keyPath))
            return JSON.DeserializeElement("null");

        var current = root.Value;
        foreach (var segment in keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return JSON.DeserializeElement("null");
        }

        return current.Clone();
    }

    public static JsonElement? DeepMerge(JsonElement? @base, JsonElement? overlay) =>
        VariableJsonMerge.ApplyPatch(@base, overlay);

    private static Dictionary<string, StageVariables>? MergeStages(
        Dictionary<string, StageVariables>? @base,
        Dictionary<string, StageVariables>? overlay)
    {
        if (@base is null && overlay is null) return null;

        var stages = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);

        if (@base is not null)
        {
            foreach (var (stage, stageVars) in @base)
                stages[stage] = stageVars.Copy();
        }

        if (overlay is not null)
        {
            foreach (var (stage, stageVars) in overlay)
            {
                if (stages.TryGetValue(stage, out var existing))
                {
                    stages[stage] = new StageVariables(VariableJsonMerge.ApplyPatch(existing.Vars, stageVars.Vars));
                }
                else
                {
                    stages[stage] = new StageVariables(
                        stageVars.Vars.HasValue ? VariableJsonMerge.ClonePatchDocument(stageVars.Vars.Value) : null);
                }
            }
        }

        return stages;
    }

    public static readonly JsonSerializerOptions JsonOptions = JSON.Options;
}

[GenerateSerializer]
public sealed record StageVariables(
    [property: Id(0)] JsonElement? Vars = null)
{
    public bool IsEmpty => !Vars.HasValue;

    public StageVariables Copy() =>
        new(Vars.HasValue ? Vars.Value.Clone() : null);
}
