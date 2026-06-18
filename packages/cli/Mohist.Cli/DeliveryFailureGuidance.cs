using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mohist.Cli;

internal static class DeliveryFailureGuidance
{
    public const string Conflict = "conflict";
    public const string BaseMoved = "base-moved";
    public const string RetrySafe = "retry-safe";

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
        };

    public static readonly IReadOnlyList<string> AllKinds = new[] { Conflict, BaseMoved, RetrySafe };

    private static readonly Regex KindInMessage = new(@"\((conflict|base-moved|retry-safe)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ResolveFailureKind(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = KindInMessage.Match(message);
        if (!match.Success) return null;
        var kind = match.Groups[1].Value;
        return AllKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ? kind.ToLowerInvariant() : null;
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

    public static (string? FailureKind, (string Label, string NextAction)? Guidance) Resolve(
        string? message,
        JsonNode? output)
    {
        var kind = ResolveFailureKind(output) ?? ResolveFailureKind(message);
        return (kind, ResolveGuidance(kind));
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

            if (obj.TryGetPropertyValue("output", out var nested))
            {
                var found = ExtractFailureKind(nested);
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
}
