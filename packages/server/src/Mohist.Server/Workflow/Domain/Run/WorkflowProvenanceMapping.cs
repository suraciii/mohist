using System.Text.Json;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// Maps settled task/check reports to provenance facts. A fact is produced
/// only when <em>both</em> the declared Action identity (<c>uses</c>) and
/// the report's structured <c>output</c> object match a known evidence
/// shape — report free text (<see cref="TaskReport.Detail"/>, messages,
/// agent-authored output such as <c>mohist/agent</c> results) never
/// produces a fact.
/// </summary>
internal static class WorkflowProvenanceMapping
{
    /// <summary>Action identity required for each structured output kind.</summary>
    private static readonly IReadOnlyDictionary<string, string> RequiredUsesByOutputKind =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["push"] = "mohist/push",
            ["create-github-pr"] = "mohist/create-github-pr",
            ["enable-github-pr-auto-merge"] = "mohist/enable-github-pr-auto-merge",
            ["github-pr-checks"] = "mohist/github-pr-checks",
            ["github-pr-status"] = "mohist/github-pr-status",
        };

    public static IReadOnlyList<WorkflowProvenanceRecorded> FromTaskReport(
        string stage,
        int attempt,
        string actionAttemptId,
        string? uses,
        string? workerId,
        TaskReport report)
    {
        if (report.Status != TaskReportStatus.Succeeded
            || report.Output is not { ValueKind: JsonValueKind.Object } output
            || uses is null
            || !TryReadString(output, "kind", out var kind)
            || !RequiredUsesByOutputKind.TryGetValue(kind, out var requiredUses)
            || !string.Equals(uses, requiredUses, StringComparison.Ordinal)
            || !TryReadString(output, "status", out var status))
        {
            return [];
        }

        if (kind == "github-pr-checks" && status == "verified")
        {
            if (!output.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
                return [];

            var facts = new List<WorkflowProvenanceRecorded>();
            foreach (var check in checks.EnumerateArray())
            {
                if (check.ValueKind != JsonValueKind.Object)
                    return [];
                var bucket = ReadString(check, "bucket");
                if (bucket is not ("pass" or "skip"))
                    return [];
                facts.Add(Fact(
                    bucket == "pass" ? WorkflowProvenanceActions.CiPassed : WorkflowProvenanceActions.CiSkipped,
                    stage, attempt, actionAttemptId, workerId, requiredUses,
                    outcome: bucket == "pass" ? "passed" : "skipped",
                    result: ReadString(check, "name"),
                    target: ReadNumber(output, "prNumber")));
            }
            return facts;
        }

        var fact = kind switch
        {
            "push" when status == "completed" && TryReadBool(output, "pushed", out var pushed) && pushed =>
                Fact(
                    WorkflowProvenanceActions.Push, stage, attempt, actionAttemptId, workerId, requiredUses,
                    outcome: "completed",
                    result: ReadString(output, "landedCommit"),
                    target: JoinRemoteTarget(output)),
            "create-github-pr" when status == "completed" && ReadString(output, "operation") is { } operation =>
                Fact(
                    WorkflowProvenanceActions.PullRequest, stage, attempt, actionAttemptId, workerId, requiredUses,
                    outcome: operation,
                    result: ReadNumber(output, "prNumber"),
                    target: ReadString(output, "prUrl")),
            "enable-github-pr-auto-merge" when status == "completed" =>
                Fact(
                    WorkflowProvenanceActions.Merge, stage, attempt, actionAttemptId, workerId, requiredUses,
                    outcome: "merged",
                    result: ReadString(output, "mergeCommitSha"),
                    target: ReadString(output, "prUrl") ?? ReadNumber(output, "prNumber")),
            _ => null,
        };

        return fact is null ? [] : [fact];
    }

    /// <summary>
    /// Maps a single settled check result. The <paramref name="uses"/> is
    /// the declared check Action from the stage definition; the merge fact
    /// requires the typed <c>github-pr-status</c> output to verify the
    /// <c>merged</c> expectation against the observed MERGED state.
    /// </summary>
    public static IReadOnlyList<WorkflowProvenanceRecorded> FromCheckResult(
        string stage,
        int attempt,
        string? uses,
        string? workerId,
        CheckResult result)
    {
        if (result.Status != CheckResultStatus.Passed
            || result.Output is not { ValueKind: JsonValueKind.Object } output
            || uses is null
            || !TryReadString(output, "kind", out var kind)
            || !RequiredUsesByOutputKind.TryGetValue(kind, out var requiredUses)
            || !string.Equals(uses, requiredUses, StringComparison.Ordinal)
            || !TryReadString(output, "status", out var status))
        {
            return [];
        }

        if (kind == "github-pr-status"
            && status == "verified"
            && ReadStringArray(output, "expectations").Contains("merged", StringComparer.Ordinal)
            && string.Equals(ReadString(output, "prState"), "MERGED", StringComparison.Ordinal))
        {
            return
            [
                Fact(
                    WorkflowProvenanceActions.Merge, stage, attempt, result.Name, workerId, requiredUses,
                    outcome: "verified",
                    result: ReadNumber(output, "prNumber"),
                    target: ReadString(output, "prUrl")),
            ];
        }

        return [];
    }

    private static WorkflowProvenanceRecorded Fact(
        string action,
        string stage,
        int attempt,
        string taskId,
        string? workerId,
        string uses,
        string outcome,
        string? result = null,
        string? target = null) =>
        new(
            action,
            WorkflowProvenanceActorKinds.System,
            workerId ?? "workflow",
            uses,
            outcome,
            Stage: stage,
            TaskId: taskId,
            Attempt: attempt,
            Result: result,
            Target: target);

    private static string? JoinRemoteTarget(JsonElement output)
    {
        var remote = ReadString(output, "remote");
        var target = ReadString(output, "target");
        return (remote, target) switch
        {
            (not null, not null) => $"{remote}/{target}",
            (null, not null) => target,
            _ => remote,
        };
    }

    private static bool TryReadString(JsonElement output, string property, out string value)
    {
        value = ReadString(output, property)!;
        return value is not null;
    }

    private static string? ReadString(JsonElement output, string property) =>
        output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNumber(JsonElement output, string property) =>
        output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText()
            : null;

    private static bool TryReadBool(JsonElement output, string property, out bool value)
    {
        if (output.TryGetProperty(property, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        value = false;
        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement output, string property)
    {
        if (!output.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
                values.Add(value);
        }

        return values;
    }
}
