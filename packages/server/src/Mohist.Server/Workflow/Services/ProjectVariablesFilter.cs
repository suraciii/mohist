using System.Text.Json;
using System.Text.Json.Nodes;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Filters legacy runtime/liveness keys from project-level <c>vars.agent</c>
/// and per-stage agent blocks at the project-variables write boundary.
/// The project-layer PATCH/PUT endpoints accept a free-shape
/// <see cref="VariableBundle"/>; without this filter, callers could
/// stamp <c>type</c>, <c>livenessQuietThresholdMs</c>, etc. into
/// <c>vars.agent</c>. The filter projects both the top-level agent block
/// and each per-stage agent block down to the converged
/// <c>{model, variant}</c> whitelist so legacy keys never enter the
/// bundle from the project write path. Already-persisted legacy keys
/// elsewhere (issue-level vars.agent, global config.jsonc) remain
/// untouched — this filter applies only to the project-layer write
/// boundary.
/// </summary>
public static class ProjectVariablesFilter
{
    public static VariableBundle Sanitize(VariableBundle bundle)
    {
        if (bundle.Vars is null && (bundle.Stages is null || bundle.Stages.Count == 0))
            return bundle;

        var newVars = bundle.Vars.HasValue ? FilterAgentInVars(bundle.Vars.Value) : bundle.Vars;
        var newStages = FilterStages(bundle.Stages);

        var varsChanged = !NullableJsonElementEquals(newVars, bundle.Vars);
        var stagesChanged = !ReferenceEquals(newStages, bundle.Stages);

        if (!varsChanged && !stagesChanged) return bundle;

        return new VariableBundle(newVars, newStages);
    }

    private static JsonElement? FilterAgentInVars(JsonElement vars)
    {
        if (vars.ValueKind != JsonValueKind.Object) return vars;
        if (!vars.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object)
            return vars;

        var filtered = FilterAgentElement(agent);
        if (filtered.HasValue && filtered.Value.ValueKind == JsonValueKind.Object
            && filtered.Value.GetRawText() == agent.GetRawText())
        {
            return vars;
        }

        var dict = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var prop in vars.EnumerateObject())
        {
            if (string.Equals(prop.Name, "agent", StringComparison.Ordinal))
            {
                dict[prop.Name] = filtered.HasValue && filtered.Value.ValueKind != JsonValueKind.Null
                    ? JsonNode.Parse(filtered.Value.GetRawText())
                    : null;
            }
            else
            {
                dict[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        return JsonSerializer.SerializeToElement(dict, WorkflowVariableJson.Options);
    }

    private static Dictionary<string, StageVariables>? FilterStages(Dictionary<string, StageVariables>? stages)
    {
        if (stages is null || stages.Count == 0) return stages;
        var changed = false;
        var result = new Dictionary<string, StageVariables>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in stages)
        {
            if (kvp.Value.Vars is null)
            {
                result[kvp.Key] = kvp.Value;
                continue;
            }

            var filtered = FilterAgentInVars(kvp.Value.Vars.Value);
            if (NullableJsonElementEquals(filtered, kvp.Value.Vars))
            {
                result[kvp.Key] = kvp.Value;
            }
            else
            {
                changed = true;
                result[kvp.Key] = new StageVariables(filtered);
            }
        }
        return changed ? result : stages;
    }

    private static JsonElement? FilterAgentElement(JsonElement agent)
    {
        Dictionary<string, object?>? filteredDict = null;
        foreach (var key in new[] { "model", "variant" })
        {
            if (!agent.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Null) continue;
            filteredDict ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            filteredDict[key] = JsonSerializer.Deserialize<object?>(value.GetRawText(), WorkflowVariableJson.Options);
        }

        if (filteredDict is null) return null;
        return JsonSerializer.SerializeToElement(filteredDict, WorkflowVariableJson.Options);
    }

    private static bool NullableJsonElementEquals(JsonElement? a, JsonElement? b)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return a.Value.GetRawText() == b.Value.GetRawText();
    }
}
