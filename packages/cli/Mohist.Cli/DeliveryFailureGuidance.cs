using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mohist.Cli;

internal static class DeliveryFailureGuidance
{
    public const string Conflict = "conflict";
    public const string BaseMoved = "base-moved";
    public const string RetrySafe = "retry-safe";
    public const string BranchInvariantViolation = "branch-invariant-violation";
    public const string WorkspaceMissing = "workspace-missing";
    public const string WorkspaceCorrupt = "workspace-corrupt";
    public const string WorkspaceIdentityMismatch = "workspace-identity-mismatch";
    public const string ConfigError = "config-error";
    public const string ProtectionConflict = "protection-conflict";
    public const string PrStateConflict = "pr-state-conflict";

    private static readonly Dictionary<string, (string Label, string NextAction)> Guidance =
        new(StringComparer.Ordinal)
        {
            [Conflict] = (
                Label: "Conflict needs attention",
                NextAction: "Conflicts could not be resolved automatically. Inspect the conflicting files, resolve them on the issue branch, and rerun prepare."),
            [BaseMoved] = (
                Label: "Base branch moved",
                NextAction: "The base branch moved during publish. Prepare the branch again, then publish."),
            [RetrySafe] = (
                Label: "Transient failure",
                NextAction: "Retry the task — the failure is unrelated to conflicts or base movement."),
            [BranchInvariantViolation] = (
                Label: "Runner / action branch-invariant violation",
                NextAction: "This is a runner or action bug: the workflow workspace left its expected run branch. Retry the task — the runner will restore the run branch automatically — and report the issue if it recurs. Issue work is not the cause."),
            [WorkspaceMissing] = (
                Label: "Workflow workspace materialization failure",
                NextAction: "The runner could not find the workflow workspace bound to this run. Issue work is not the cause — the workflow-start materialization pipeline must be repaired (rebind the workspace, or investigate the runner's workspace root) before this run can continue."),
            [WorkspaceCorrupt] = (
                Label: "Workflow workspace materialization failure",
                NextAction: "The runner's workflow workspace is unreadable or its workspace marker is missing/corrupt. Issue work is not the cause — re-materialize the workflow workspace at the run's bound path before this run can continue."),
            [WorkspaceIdentityMismatch] = (
                Label: "Workflow workspace materialization failure",
                NextAction: "The workflow workspace at the run's bound path belongs to a different workflow run. Issue work is not the cause — re-bind a fresh workflow workspace to this run before it can continue."),
            [ConfigError] = (
                Label: "Runner environment is misconfigured",
                NextAction: "Install the GitHub CLI (`gh`) on the runner host and run `gh auth login` to authenticate with GitHub. Then re-run the issue. The workflow will not auto-retry this kind — environment fixes need a human before the next attempt."),
            [ProtectionConflict] = (
                Label: "Branch protection blocked the merge",
                NextAction: "GitHub rejected the merge because branch protection requires status checks or reviews that this run cannot satisfy. Adjust the repository's branch-protection rules (or switch this issue to the `mohist/default` workflow) and re-run. The workflow will not auto-retry this kind."),
            [PrStateConflict] = (
                Label: "Pull request state changed externally",
                NextAction: "The pull request was closed or its state changed outside the runner between workflow steps (for example, by a human via the GitHub UI). Decide whether to re-open the PR or abandon it, then re-run or close the issue. The workflow will not auto-retry this kind."),
        };

    public static readonly IReadOnlyList<string> AllKinds = new[]
    {
        Conflict,
        BaseMoved,
        RetrySafe,
        BranchInvariantViolation,
        WorkspaceMissing,
        WorkspaceCorrupt,
        WorkspaceIdentityMismatch,
        ConfigError,
        ProtectionConflict,
        PrStateConflict,
    };

    public static readonly IReadOnlyList<string> WorkspaceMaterializationKinds = new[]
    {
        WorkspaceMissing,
        WorkspaceCorrupt,
        WorkspaceIdentityMismatch,
    };

    private static readonly Regex KindInMessage = new(
        @"\((conflict|base-moved|retry-safe|branch-invariant-violation|workspace-missing|workspace-corrupt|workspace-identity-mismatch|config-error|protection-conflict|pr-state-conflict)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BranchInvariantInMessage = new(
        @"\bbranch-invariant\s+violation\b(?:\s+at\s+(?<boundary>start|end)\s+boundary)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BranchEvidenceInMessage = new(
        @"expected\s+branch\s+'(?<expected>[^']*)'.*?observed\s+(?:'(?<observed>[^']*)'|detached\s+at\s+(?<ref>[^\s\)]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public sealed record BranchEvidence(
        string? ExpectedBranch,
        string? ObservedBranch,
        string? Boundary,
        string? ObservedRef);

    public sealed record WorkspaceEvidence(
        string? WorkspacePath,
        string? ExpectedRunId,
        string? ActualRunId);

    public static bool IsWorkspaceMaterializationKind(string? failureKind)
    {
        if (string.IsNullOrEmpty(failureKind)) return false;
        return WorkspaceMaterializationKinds.Contains(failureKind, StringComparer.OrdinalIgnoreCase);
    }

    public static string? ResolveFailureKind(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = KindInMessage.Match(message);
        if (match.Success)
        {
            var kind = match.Groups[1].Value;
            return AllKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ? kind.ToLowerInvariant() : null;
        }
        if (BranchInvariantInMessage.IsMatch(message))
        {
            return BranchInvariantViolation;
        }
        return null;
    }

    public static string? ResolveFailureKind(JsonNode? output)
    {
        if (output is null) return null;
        var kind = ExtractFailureKind(output);
        if (string.IsNullOrEmpty(kind)) return null;
        return AllKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ? kind.ToLowerInvariant() : null;
    }

    public static (string Label, string NextAction)? ResolveGuidance(string? failureKind)
    {
        if (string.IsNullOrEmpty(failureKind)) return null;
        if (!Guidance.TryGetValue(failureKind, out var entry)) return null;
        return entry;
    }

    public static BranchEvidence? ResolveBranchEvidence(string? message, JsonNode? output)
    {
        if (!string.IsNullOrEmpty(message))
        {
            var fromMessage = ExtractBranchEvidenceFromMessage(message);
            if (fromMessage is not null) return fromMessage;
        }
        if (output is not null)
        {
            var fromOutput = ExtractBranchEvidenceFromOutput(output);
            if (fromOutput is not null) return fromOutput;
        }
        return null;
    }

    public static (string? FailureKind, (string Label, string NextAction)? Guidance) Resolve(
        string? message,
        JsonNode? output)
    {
        var kind = ResolveFailureKind(output) ?? ResolveFailureKind(message);
        return (kind, ResolveGuidance(kind));
    }

    public static (string? FailureKind, (string Label, string NextAction)? Guidance, BranchEvidence? Evidence) ResolveWithEvidence(
        string? message,
        JsonNode? output)
    {
        var kind = ResolveFailureKind(output) ?? ResolveFailureKind(message);
        var guidance = ResolveGuidance(kind);
        BranchEvidence? evidence = null;
        if (string.Equals(kind, BranchInvariantViolation, StringComparison.OrdinalIgnoreCase))
        {
            evidence = ResolveBranchEvidence(message, output);
        }
        return (kind, guidance, evidence);
    }

    public static (string? FailureKind, (string Label, string NextAction)? Guidance, WorkspaceEvidence? Evidence) ResolveWithWorkspaceEvidence(
        string? message,
        JsonNode? output)
    {
        var kind = ResolveFailureKind(output) ?? ResolveFailureKind(message);
        var guidance = ResolveGuidance(kind);
        WorkspaceEvidence? evidence = null;
        if (IsWorkspaceMaterializationKind(kind))
        {
            evidence = ResolveWorkspaceEvidence(message, output);
        }
        return (kind, guidance, evidence);
    }

    public static WorkspaceEvidence? ResolveWorkspaceEvidence(string? message, JsonNode? output)
    {
        if (output is not null)
        {
            var fromOutput = ExtractWorkspaceEvidenceFromOutput(output);
            if (fromOutput is not null) return fromOutput;
        }
        if (!string.IsNullOrEmpty(message))
        {
            var fromMessage = ExtractWorkspaceEvidenceFromMessage(message);
            if (fromMessage is not null) return fromMessage;
        }
        return null;
    }

    private static BranchEvidence? ExtractBranchEvidenceFromMessage(string message)
    {
        var match = BranchEvidenceInMessage.Match(message);
        if (!match.Success) return null;
        var boundaryMatch = BranchInvariantInMessage.Match(message);
        var boundary = boundaryMatch.Success ? boundaryMatch.Groups["boundary"].Value.ToLowerInvariant() : null;
        var expected = match.Groups["expected"].Success ? match.Groups["expected"].Value : string.Empty;
        var observed = match.Groups["observed"].Success && match.Groups["observed"].Value.Length > 0
            ? match.Groups["observed"].Value
            : string.Empty;
        var refValue = match.Groups["ref"].Success && match.Groups["ref"].Value.Length > 0
            ? match.Groups["ref"].Value
            : null;
        return new BranchEvidence(
            string.IsNullOrEmpty(expected) ? null : expected,
            string.IsNullOrEmpty(observed) ? string.Empty : observed,
            string.IsNullOrEmpty(boundary) ? null : boundary,
            refValue);
    }

    private static BranchEvidence? ExtractBranchEvidenceFromOutput(JsonNode output)
    {
        var evidence = FindBranchEvidenceNode(output);
        if (evidence is null) return null;
        return new BranchEvidence(
            ExpectedBranch: StringOf(evidence, "expectedBranch"),
            ObservedBranch: StringOf(evidence, "observedBranch"),
            Boundary: NormalizeBoundary(StringOf(evidence, "boundary")),
            ObservedRef: StringOf(evidence, "observedRef"));
    }

    private static JsonObject? FindBranchEvidenceNode(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var raw))
            {
                var trimmed = raw?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return null;
                try
                {
                    return FindBranchEvidenceNode(JsonNode.Parse(trimmed));
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var found = FindBranchEvidenceNode(item);
                if (found is not null) return found;
            }
            return null;
        }

        if (node is JsonObject obj)
        {
            var kind = StringOf(obj, "kind");
            if (string.Equals(kind, BranchInvariantViolation, StringComparison.OrdinalIgnoreCase))
            {
                return obj;
            }
            if (obj.TryGetPropertyValue("output", out var nested))
            {
                var found = FindBranchEvidenceNode(nested);
                if (found is not null) return found;
            }
            if (obj.TryGetPropertyValue("branchStability", out var stack))
            {
                var found = FindBranchEvidenceNode(stack);
                if (found is not null) return found;
            }
        }

        return null;
    }

    private static string? ExtractFailureKind(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var raw))
            {
                var trimmed = raw?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return null;
                try
                {
                    var parsed = JsonNode.Parse(trimmed);
                    return ExtractFailureKind(parsed);
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var found = ExtractFailureKind(item);
                if (!string.IsNullOrEmpty(found)) return found;
            }
            return null;
        }

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("failureKind", out var direct) ||
                obj.TryGetPropertyValue("FailureKind", out direct) ||
                obj.TryGetPropertyValue("errorCode", out direct) ||
                obj.TryGetPropertyValue("ErrorCode", out direct))
            {
                if (direct is JsonValue dv && dv.TryGetValue<string>(out var dvs))
                {
                    return AllKinds.Contains(dvs, StringComparer.OrdinalIgnoreCase) ? dvs.ToLowerInvariant() : null;
                }
            }

            if (obj.TryGetPropertyValue("kind", out var kindNode))
            {
                if (kindNode is JsonValue kv && kv.TryGetValue<string>(out var kvs))
                {
                    if (AllKinds.Contains(kvs, StringComparer.OrdinalIgnoreCase))
                    {
                        return kvs.ToLowerInvariant();
                    }
                }
            }

            if (obj.TryGetPropertyValue("output", out var nested))
            {
                var found = ExtractFailureKind(nested);
                if (!string.IsNullOrEmpty(found)) return found;
            }

            if (obj.TryGetPropertyValue("branchStability", out var branchStability))
            {
                var found = ExtractFailureKind(branchStability);
                if (!string.IsNullOrEmpty(found)) return found;
            }

            if (obj.TryGetPropertyValue("message", out var msgNode))
            {
                var msgString = msgNode is JsonValue mv && mv.TryGetValue<string>(out var mvs) ? mvs : null;
                var found = ResolveFailureKind(msgString);
                if (!string.IsNullOrEmpty(found)) return found;
            }
        }

        return null;
    }

    private static string? StringOf(JsonNode? node, string property)
    {
        if (node is not JsonObject obj) return null;
        if (!obj.TryGetPropertyValue(property, out var value)) return null;
        if (value is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static string? NormalizeBoundary(string? boundary)
    {
        if (string.IsNullOrEmpty(boundary)) return null;
        return boundary.ToLowerInvariant() switch
        {
            "start" => "start",
            "end" => "end",
            _ => boundary.ToLowerInvariant(),
        };
    }

    private static WorkspaceEvidence? ExtractWorkspaceEvidenceFromOutput(JsonNode output)
    {
        var node = FindWorkspaceEvidenceNode(output);
        if (node is null) return null;
        var workspacePath = StringOf(node, "workspacePath");
        var expectedRunId = StringOf(ReadIdentityNode(node, "expected"), "workflowRunId");
        var actualRunId = StringOf(ReadIdentityNode(node, "actual"), "workflowRunId");
        if (string.IsNullOrEmpty(workspacePath)
            && string.IsNullOrEmpty(expectedRunId)
            && string.IsNullOrEmpty(actualRunId))
        {
            return null;
        }
        return new WorkspaceEvidence(
            WorkspacePath: string.IsNullOrEmpty(workspacePath) ? null : workspacePath,
            ExpectedRunId: string.IsNullOrEmpty(expectedRunId) ? null : expectedRunId,
            ActualRunId: string.IsNullOrEmpty(actualRunId) ? null : actualRunId);
    }

    private static WorkspaceEvidence? ExtractWorkspaceEvidenceFromMessage(string message)
    {
        // The runner emits `workflow workspace materialization failure
        // (<kind>): <explanation>`. The structured output is the source
        // of truth for workspacePath / expected / actual; the message
        // is only a fallback when structured output is unavailable.
        return null;
    }

    private static JsonObject? FindWorkspaceEvidenceNode(JsonNode? node)
    {
        if (node is null) return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var raw))
            {
                var trimmed = raw?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return null;
                try
                {
                    return FindWorkspaceEvidenceNode(JsonNode.Parse(trimmed));
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            return null;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var found = FindWorkspaceEvidenceNode(item);
                if (found is not null) return found;
            }
            return null;
        }

        if (node is JsonObject obj)
        {
            var kind = StringOf(obj, "kind");
            if (IsWorkspaceMaterializationKind(kind))
            {
                return obj;
            }
            if (obj.TryGetPropertyValue("output", out var nested))
            {
                var found = FindWorkspaceEvidenceNode(nested);
                if (found is not null) return found;
            }
        }

        return null;
    }

    private static JsonNode? ReadIdentityNode(JsonObject? parent, string property)
    {
        if (parent is null) return null;
        if (!parent.TryGetPropertyValue(property, out var value)) return null;
        return value;
    }
}
