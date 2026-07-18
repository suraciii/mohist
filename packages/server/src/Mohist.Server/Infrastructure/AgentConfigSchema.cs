using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Server.Infrastructure;

/// <summary>
/// Converged AgentConfig schema shared by the issue-level and
/// agent-definition write surfaces. Per #410 T-002 design D5, the
/// issue-level and agent-definition <c>agentConfig</c> surfaces accept
/// only <c>model</c> + <c>variant</c>; legacy ACP/liveness keys
/// (<c>type</c>, <c>livenessQuietThresholdMs</c>, <c>probeTimeoutMs</c>,
/// <c>sessionStartTimeoutMs</c>, <c>compaction</c>) are rejected at the
/// API boundary with an actionable validation error and never enter the
/// persisted bundle. Stored legacy keys remain in storage (no data
/// rewrite); the mohist/opencode runtime's <c>unknownKeys</c> diagnostic
/// path covers them when they reach an execution request.
/// </summary>
public static class AgentConfigSchema
{
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "variant",
    };

    public static readonly IReadOnlySet<string> ForbiddenKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "type",
        "livenessQuietThresholdMs",
        "probeTimeoutMs",
        "sessionStartTimeoutMs",
        "compaction",
    };

    /// <summary>
    /// Validate the open-shape <c>agentConfig</c> body. Returns
    /// <c>null</c> when every key is in the allowed whitelist;
    /// otherwise returns the first offending key in a user-facing
    /// message. The function accepts the raw JSON element so the route
    /// layer can read presence without a re-deserialization round trip.
    /// </summary>
    public static string? Validate(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in agentConfig.Value.EnumerateObject())
        {
            if (ForbiddenKeys.Contains(property.Name) || !AllowedKeys.Contains(property.Name))
            {
                return $"agentConfig.{property.Name} is not allowed; the agent config accepts only {string.Join(", ", AllowedKeys)}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Project the open-shape <c>agentConfig</c> body down to the
    /// converged whitelist. Returns <c>null</c> when the input is null,
    /// not a JSON object, or when no allowed keys survive the projection.
    /// </summary>
    public static Dictionary<string, object?>? Project(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind != JsonValueKind.Object)
            return null;

        Dictionary<string, object?>? result = null;
        foreach (var property in agentConfig.Value.EnumerateObject())
        {
            if (!AllowedKeys.Contains(property.Name)) continue;
            result ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return result;
    }

    /// <summary>
    /// Project an already-deserialized <see cref="Dictionary{TKey,TValue}"/>
    /// down to the converged whitelist. Used by write-side merge paths
    /// (IssueVariableBuilder, MohistIssueWorkflowProfileBase,
    /// ConfigService) that hold agent config as a plain dictionary.
    /// </summary>
    public static Dictionary<string, object?>? Filter(IDictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || agentConfig.Count == 0) return null;
        Dictionary<string, object?>? result = null;
        foreach (var key in new[] { "model", "variant" })
        {
            if (!agentConfig.TryGetValue(key, out var value) || value is null) continue;
            result ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            result[key] = value;
        }
        return result;
    }
}
