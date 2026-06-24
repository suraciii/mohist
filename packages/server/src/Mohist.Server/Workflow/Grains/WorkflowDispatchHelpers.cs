using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

internal static class WorkflowDispatchHelpers
{
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
