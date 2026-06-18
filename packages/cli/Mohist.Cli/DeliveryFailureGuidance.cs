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
        };

    public static readonly IReadOnlyList<string> AllKinds = new[] { Conflict, BaseMoved, RetrySafe, BranchInvariantViolation };

    private static readonly Regex KindInMessage = new(
        @"\((conflict|base-moved|retry-safe|branch-invariant-violation)\)",
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
                obj.TryGetPropertyValue("FailureKind", out direct))
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
                    if (string.Equals(kvs, BranchInvariantViolation, StringComparison.OrdinalIgnoreCase))
                    {
                        return BranchInvariantViolation;
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
}
