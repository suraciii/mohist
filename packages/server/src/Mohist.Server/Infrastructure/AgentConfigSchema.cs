using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Server.Infrastructure;

/// <summary>
/// Converged AgentConfig schema shared by the issue-level and
/// agent-definition write surfaces. The
/// issue-level <c>agentConfig</c> surfaces accept <c>model</c> +
/// <c>variant</c> + <c>reasoningEffort</c>; Agent-definition surfaces
/// additionally accept <c>runtime</c>. Legacy runtime/liveness keys
/// (<c>type</c>, <c>livenessQuietThresholdMs</c>, <c>probeTimeoutMs</c>,
/// <c>sessionStartTimeoutMs</c>, <c>compaction</c>) are rejected at the
/// API boundary with an actionable validation error and never enter the
/// persisted bundle. Stored legacy keys remain in storage (no data
/// rewrite); the mohist/opencode runtime's <c>unknownKeys</c> diagnostic
/// path covers them when they reach an execution request.
///
/// <para>
/// The whitelist also carries the execution
/// backend dimension (<c>runtime</c>: <c>opencode</c> | <c>pi</c>).
/// Absent / unset resolves to <c>opencode</c>; any other value is
/// rejected as invalid. Agent CRUD uses <see cref="Validate"/> and Issue
/// configuration uses <see cref="ValidateIssue"/> so runtime has one owner.
/// </para>
/// </summary>
public static class AgentConfigSchema
{
    public const string OpenCodeRuntime = "opencode";
    public const string PiRuntime = "pi";

    public static readonly IReadOnlySet<string> AllowedRuntimes = new HashSet<string>(StringComparer.Ordinal)
    {
        OpenCodeRuntime,
        PiRuntime,
    };

    /// <summary>
    /// Canonical reasoning-effort vocabulary: the value space accepted by
    /// every write surface for the Agent-execution configuration key
    /// <c>reasoningEffort</c>. Independent from <c>variant</c> — an effort
    /// is never encoded as a variant and a variant is never interpreted as
    /// an effort. Validation is canonical-set only: no write-time check
    /// against a runner catalog. The ordered list keeps user-facing error
    /// messages stable while the set powers membership checks.
    /// </summary>
    public static readonly IReadOnlyList<string> CanonicalReasoningEffortsOrdered =
    [
        "off",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
    ];

    public static readonly IReadOnlySet<string> CanonicalReasoningEfforts =
        new HashSet<string>(CanonicalReasoningEffortsOrdered, StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "variant",
        "reasoningEffort",
        "runtime",
    };

    public static readonly IReadOnlySet<string> IssueAllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "variant",
        "reasoningEffort",
    };

    /// <summary>
    /// Stable display order for <see cref="IssueAllowedKeys"/> so the
    /// issue-surface "accepts only …" error message reads deterministically.
    /// </summary>
    private static readonly string CanonicalIssueKeyOrder = "model, variant, reasoningEffort";

    public static string? ValidateIssue(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in agentConfig.Value.EnumerateObject())
        {
            if (!IssueAllowedKeys.Contains(property.Name))
                return property.Name == "runtime"
                    ? "agentConfig.runtime is not supported for Issue configuration; configure runtime on the Agent definition."
                    : $"agentConfig.{property.Name} is not allowed; Issue agent config accepts only {string.Join(", ", CanonicalIssueKeyOrder)}.";
        }

        // The issue surface shares the canonical reasoning-effort value
        // validation with the Agent-definition surface so both write paths
        // enforce one vocabulary.
        return ValidateReasoningEffort(agentConfig.Value);
    }

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
    /// <c>null</c> when every key is in the allowed whitelist and every
    /// known-enum key carries a valid value; otherwise returns the first
    /// offending key (or value) in a user-facing message. The function
    /// accepts the raw JSON element so the route layer can read presence
    /// without a re-deserialization round trip.
    /// </summary>
    public static string? Validate(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind == JsonValueKind.Null)
            return null;
        if (agentConfig.Value.ValueKind != JsonValueKind.Object)
            return "agentConfig must be a JSON object or null.";

        foreach (var property in agentConfig.Value.EnumerateObject())
        {
            if (ForbiddenKeys.Contains(property.Name) || !AllowedKeys.Contains(property.Name))
            {
                return $"agentConfig.{property.Name} is not allowed; the agent config accepts only {string.Join(", ", AllowedKeys)}.";
            }

            if (property.Name is "model" or "variant" or "reasoningEffort"
                && property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return $"agentConfig.{property.Name} must be a string or null.";
            }

            if (property.Name is "model" or "variant" or "reasoningEffort"
                && property.Value.ValueKind == JsonValueKind.String
                && string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return $"agentConfig.{property.Name} must not be empty.";
            }
        }

        return ValidateRuntime(agentConfig.Value) ?? ValidateReasoningEffort(agentConfig.Value);
    }

    /// <summary>
    /// Validate the <c>runtime</c> field when present. An absent key is
    /// valid (the resolver defaults to <see cref="OpenCodeRuntime"/>); a
    /// non-string or out-of-set value is rejected with an actionable
    /// message that lists the accepted backends. Lives separately so the
    /// write-side projection paths can apply the same value check on a
    /// dictionary shape.
    /// </summary>
    public static string? ValidateRuntime(JsonElement agentConfig)
    {
        if (agentConfig.ValueKind != JsonValueKind.Object) return null;
        if (!agentConfig.TryGetProperty("runtime", out var runtime)) return null;
        if (runtime.ValueKind == JsonValueKind.Null) return null;
        if (runtime.ValueKind != JsonValueKind.String)
        {
            return "agentConfig.runtime must be one of opencode, pi.";
        }
        var raw = runtime.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "agentConfig.runtime must be one of opencode, pi.";
        }
        if (!AllowedRuntimes.Contains(raw))
        {
            return $"agentConfig.runtime '{raw}' is not supported; the agent runtime accepts only {string.Join(", ", AllowedRuntimes)}.";
        }
        return null;
    }

    /// <summary>
    /// Validate the <c>runtime</c> field on an already-deserialized
    /// dictionary. Same semantics as the JsonElement overload; an absent
    /// key is valid.
    /// </summary>
    public static string? ValidateRuntime(IDictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || !agentConfig.TryGetValue("runtime", out var value) || value is null)
            return null;
        if (value is not string raw)
            return "agentConfig.runtime must be one of opencode, pi.";
        if (!AllowedRuntimes.Contains(raw))
            return $"agentConfig.runtime '{raw}' is not supported; the agent runtime accepts only {string.Join(", ", AllowedRuntimes)}.";
        return null;
    }

    /// <summary>
    /// Validate the <c>reasoningEffort</c> field when present. An absent
    /// or null key is valid (unset effort); a non-string, empty, or
    /// non-canonical value is rejected with an actionable message that
    /// names every accepted value — mirroring the
    /// <see cref="ValidateRuntime"/> message style. Lives beside
    /// <see cref="ValidateRuntime"/> so both write surfaces (Agent
    /// definition via <see cref="Validate"/>, Issue override via
    /// <see cref="ValidateIssue"/>) enforce the same canonical
    /// vocabulary. Canonical-set validation only — never checked against a
    /// runner catalog at write time.
    /// </summary>
    public static string? ValidateReasoningEffort(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind != JsonValueKind.Object) return null;
        if (!agentConfig.Value.TryGetProperty("reasoningEffort", out var effort)) return null;
        if (effort.ValueKind == JsonValueKind.Null) return null;
        if (effort.ValueKind != JsonValueKind.String)
            return "agentConfig.reasoningEffort must be a string or null.";
        var raw = effort.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return "agentConfig.reasoningEffort must not be empty.";
        if (!CanonicalReasoningEfforts.Contains(raw))
        {
            return $"agentConfig.reasoningEffort '{raw}' is not supported; the reasoning effort accepts only {string.Join(", ", CanonicalReasoningEffortsOrdered)}.";
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
    /// Iterates <see cref="AllowedKeys"/> so the projection tracks the
    /// validation whitelist — adding a new key to the validation surface
    /// automatically flows into the write-side merge.
    /// </summary>
    public static Dictionary<string, object?>? Filter(IDictionary<string, object?>? agentConfig)
    {
        if (agentConfig is null || agentConfig.Count == 0) return null;
        Dictionary<string, object?>? result = null;
        foreach (var key in IssueAllowedKeys)
        {
            if (!agentConfig.TryGetValue(key, out var value) || value is null) continue;
            result ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            result[key] = value;
        }
        return result;
    }
}

public static class ReasoningEfforts
{
    public const string Off = "off";
    public const string Minimal = "minimal";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string XHigh = "xhigh";
    public const string Max = "max";

    public static readonly IReadOnlyList<string> All =
    [
        Off,
        Minimal,
        Low,
        Medium,
        High,
        XHigh,
        Max,
    ];

    public static bool Contains(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}
