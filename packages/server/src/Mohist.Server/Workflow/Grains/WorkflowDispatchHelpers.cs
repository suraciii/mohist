using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

internal static class WorkflowDispatchHelpers
{
    internal static JsonElement? ResolveEffectiveStageVars(VariableBundle resolved, string? stage)
    {
        if (!resolved.Vars.HasValue && resolved.Stages is null) return null;

        var effective = resolved.Vars.HasValue && resolved.Vars.Value.ValueKind == JsonValueKind.Object
            ? resolved.Vars.Value
            : JSON.DeserializeElement("{}");

        if (resolved.Stages is not null
            && !string.IsNullOrWhiteSpace(stage)
            && resolved.Stages.TryGetValue(stage, out var stageVars)
            && stageVars.Vars.HasValue
            && stageVars.Vars.Value.ValueKind == JsonValueKind.Object)
        {
            var stageOverlay = JSON.DeserializeElement(JSON.Serialize(stageVars.Vars.Value));
            effective = DeepMergeSkippingNulls(effective, stageOverlay) ?? stageOverlay;
        }

        return effective;
    }

    internal static JsonElement? DeepMergeSkippingNulls(JsonElement? @base, JsonElement? overlay)
    {
        if (!overlay.HasValue) return @base;
        if (overlay.Value.ValueKind == JsonValueKind.Null) return @base;
        if (!@base.HasValue) return overlay.Value.Clone();

        if (@base.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();
        if (overlay.Value.ValueKind != JsonValueKind.Object)
            return overlay.Value.Clone();

        using var baseDoc = JsonDocument.Parse(@base.Value.GetRawText());
        using var overlayDoc = JsonDocument.Parse(overlay.Value.GetRawText());
        var merged = MergeObjectsSkippingNulls(baseDoc.RootElement, overlayDoc.RootElement);
        return JSON.DeserializeElement(JSON.Serialize(merged));
    }

    internal static Dictionary<string, object?> MergeObjectsSkippingNulls(JsonElement @base, JsonElement overlay)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in @base.EnumerateObject())
            result[property.Name] = JsonElementToObject(property.Value);

        foreach (var property in overlay.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
                continue;

            if (property.Value.ValueKind == JsonValueKind.Object
                && @base.TryGetProperty(property.Name, out var existing)
                && existing.ValueKind == JsonValueKind.Object)
            {
                result[property.Name] = MergeObjectsSkippingNulls(existing, property.Value);
                continue;
            }

            result[property.Name] = JsonElementToObject(property.Value);
        }

        return result;
    }

    internal static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDouble(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    internal static JsonElement BuildRuntimeVariablesElement(IReadOnlyDictionary<string, JsonElement> runtimeVariables)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in runtimeVariables)
        {
            var segments = key.Split('.');
            var current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!current.TryGetValue(segments[i], out var existing) || existing is not Dictionary<string, object?> dict)
                {
                    dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current[segments[i]] = dict;
                }
                current = dict;
            }
            current[segments[^1]] = JsonElementToObject(value.Clone());
        }
        return JSON.SerializeToElement(root);
    }

    internal static Dictionary<string, JsonElement?> JsonElementToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    internal static Dictionary<string, JsonElement?> MergeRuntimeVariablesIntoPayload(
        Dictionary<string, JsonElement?> payload,
        IReadOnlyDictionary<string, JsonElement> runtimeVariables)
    {
        var runtimeElement = BuildRuntimeVariablesElement(runtimeVariables);
        var payloadElement = JSON.SerializeToElement(payload);
        var merged = DeepMergeSkippingNulls(payloadElement, runtimeElement) ?? payloadElement;
        return JsonElementToDictionary(merged);
    }

    internal static string? TryReadNestedString(Dictionary<string, JsonElement?>? values, string key, string nestedKey)
    {
        if (values is null || !values.TryGetValue(key, out var value) || !value.HasValue)
            return null;
        return TryReadNestedString(value.Value, nestedKey);
    }

    internal static string? TryReadNestedString(JsonElement value, string key, string nestedKey)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var nested))
            return null;
        return TryReadNestedString(nested, nestedKey);
    }

    internal static string? TryReadNestedString(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var nested))
            return null;
        return nested.ValueKind == JsonValueKind.String ? nested.GetString() : null;
    }

    internal static (bool Present, string? Value) TryReadStageAgentModel(VariableBundle resolved, string stage)
    {
        if (resolved.Stages is null || !resolved.Stages.TryGetValue(stage, out var stageVars) || !stageVars.Vars.HasValue)
            return (false, null);

        var vars = stageVars.Vars.Value;
        if (vars.ValueKind != JsonValueKind.Object
            || !vars.TryGetProperty("agent", out var agent)
            || agent.ValueKind != JsonValueKind.Object
            || !agent.TryGetProperty("model", out var model))
            return (false, null);

        return model.ValueKind == JsonValueKind.String
            ? (true, model.GetString())
            : (true, null);
    }

    internal static WorkIssueRef? BuildIssueRef(Dictionary<string, JsonElement?> payload)
    {
        if (!payload.TryGetValue("project", out var projectEl) || !projectEl.HasValue) return null;
        if (!payload.TryGetValue("issue", out var issueEl) || !issueEl.HasValue) return null;
        if (projectEl.Value.ValueKind != JsonValueKind.Object) return null;
        if (issueEl.Value.ValueKind != JsonValueKind.Object) return null;

        if (!projectEl.Value.TryGetProperty("id", out var projectIdEl)) return null;
        if (!issueEl.Value.TryGetProperty("id", out var issueIdEl)) return null;
        if (!issueEl.Value.TryGetProperty("number", out var numberEl)) return null;

        var projectId = projectIdEl.ValueKind == JsonValueKind.String ? projectIdEl.GetString() : projectIdEl.GetRawText();
        var issueId = issueIdEl.ValueKind == JsonValueKind.String ? issueIdEl.GetString() : issueIdEl.GetRawText();
        var numberStr = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();

        if (projectId is null || issueId is null || !int.TryParse(numberStr, out var num))
            return null;

        return new WorkIssueRef(projectId, issueId, num);
    }

    internal static int TaskAttempt(string taskRunId)
    {
        var lastDot = taskRunId.LastIndexOf('.');
        return lastDot >= 0 && int.TryParse(taskRunId[(lastDot + 1)..], out var attempt)
            ? attempt
            : 1;
    }

    internal static void CaptureTaskOutputs(WorkflowRun run, TaskRun? task, Dictionary<string, JsonElement>? capturedOutputs)
    {
        if (task is null || capturedOutputs is null || capturedOutputs.Count == 0)
            return;

        var declaredNames = task.Outputs?.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (declaredNames is null || declaredNames.Count == 0)
            return;

        var validated = capturedOutputs
            .Where(kv => declaredNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        run.CaptureTaskOutputs(task.DefinitionId, validated);
    }

    internal static List<CheckResult> ParseCheckResults(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray().Select(ParseSingleCheckResult).Where(r => r is not null).Cast<CheckResult>().ToList();

            var single = ParseSingleCheckResult(root);
            return single is not null ? [single] : [];
        }
        catch
        {
            return [];
        }
    }

    internal static CheckResult? ParseSingleCheckResult(JsonElement element)
    {
        var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var status = element.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "fail" : "fail";
        var message = element.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
        JsonElement? output = element.TryGetProperty("output", out var outProp) ? outProp.Clone() : null;

        return new CheckResult(name!, status, message, output);
    }

    internal static Dictionary<string, JsonElement?>? ParseWith(string? with) =>
        with is not null ? JsonSerializer.Deserialize<Dictionary<string, JsonElement?>>(with, JSON.Options) : null;
}
