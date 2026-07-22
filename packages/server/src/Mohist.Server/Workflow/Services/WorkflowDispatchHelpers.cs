using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Serialization;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Definition;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Pure formatting helpers shared between the control-plane grain and the
/// runner-side <c>WorkflowItemTranslator</c>. These methods contain no
/// external dependencies and carry only invariant shapes (work-id attempt
/// parsing, check-result JSON parsing, agent-model diagnostics). Pulled
/// out of <c>WorkflowDispatchBuilder</c> so the translator (now in
/// <c>Runner.Services</c>) can reuse them without taking a dependency on
/// the old grain-side builder.
/// </summary>
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
        if (!payload.TryGetValue("issue", out var issueEl) || !issueEl.HasValue) return null;
        if (issueEl.Value.ValueKind != JsonValueKind.Object) return null;

        if (!issueEl.Value.TryGetProperty("projectId", out var projectIdEl)) return null;
        if (!issueEl.Value.TryGetProperty("number", out var numberEl)) return null;

        var projectId = projectIdEl.ValueKind == JsonValueKind.String ? projectIdEl.GetString() : projectIdEl.GetRawText();
        var numberStr = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();

        if (projectId is null || !int.TryParse(numberStr, out var num))
            return null;

        return new WorkIssueRef(projectId, num);
    }

    internal static int TaskAttempt(string taskRunId)
    {
        var lastDot = taskRunId.LastIndexOf('.');
        return lastDot >= 0 && int.TryParse(taskRunId[(lastDot + 1)..], out var attempt)
            ? attempt
            : 1;
    }

    internal static List<CheckResult> ParseCheckResults(JsonElement? output)
    {
        if (!output.HasValue) return [];
        var root = output.Value;

        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(ParseSingleCheckResult).Where(r => r is not null).Cast<CheckResult>().ToList();

        return [];
    }

    internal static CheckResult? ParseSingleCheckResult(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var status = element.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;
        var message = element.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
        JsonElement? output = element.TryGetProperty("output", out var outProp) ? outProp.Clone() : null;
        ExecutionError? error = null;
        if (element.TryGetProperty("error", out var errorProp)
            && errorProp.ValueKind == JsonValueKind.Object
            && errorProp.TryGetProperty("code", out var codeProp)
            && errorProp.TryGetProperty("message", out var errorMessageProp))
        {
            var code = codeProp.GetString();
            var errorMessage = errorMessageProp.GetString();
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(errorMessage))
                error = new ExecutionError(code, errorMessage);
        }

        return new CheckResult(name!, ParseCheckResultStatus(status), message, output, error);
    }

    private static CheckResultStatus ParseCheckResultStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "pass" or "passed" => CheckResultStatus.Passed,
            "pending" => CheckResultStatus.Pending,
            "fail" or "failed" => CheckResultStatus.Failed,
            _ => CheckResultStatus.Failed
        };

    internal static Dictionary<string, JsonElement?>? ParseWith(JsonElement? with) =>
        with is { } el ? el.Deserialize<Dictionary<string, JsonElement?>>(JSON.Options) : null;

    /// <summary>
    /// Projects the most recent task outputs of the given workflow run
    /// onto the dispatch payload under the <c>tasks</c> key. Tasks whose
    /// output is not an object (string, array, etc.) are skipped. Used by
    /// the runner-side <c>WorkflowItemTranslator</c> when assembling the
    /// runner execution envelope; kept as a shared helper so it stays
    /// unit-testable without spinning up a runner grain.
    /// </summary>
    internal static void MergeTaskOutputsIntoPayload(Dictionary<string, JsonElement?> payload, WorkflowRun run)
    {
        var tasksMap = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var stage in run.Stages)
        {
            foreach (var task in stage.Tasks)
            {
                if (task.Status != TaskRunStatus.Completed || !task.Output.HasValue)
                    continue;

                if (task.Output.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var outputs = JsonElementToObject(task.Output.Value);
                if (outputs is Dictionary<string, object?> dict)
                {
                    tasksMap[task.DefinitionId] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outputs"] = dict
                    };
                }
            }
        }

        if (tasksMap.Count > 0)
        {
            payload["tasks"] = JSON.SerializeToElement(tasksMap);
        }
    }
}
