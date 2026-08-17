using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mohist.Server.Infrastructure;

/// <summary>
/// Converged AgentConfig schema shared by the issue-level and
/// agent-definition write surfaces. The
/// issue-level <c>agentConfig</c> surfaces accept only <c>model</c> +
/// model level fields; Agent-definition surfaces additionally accept
/// <c>runtime</c>. Legacy runtime/liveness keys
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

    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "reasoningEffort",
        "variant",
        "runtime",
    };

    public static readonly IReadOnlySet<string> IssueAllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "model",
        "reasoningEffort",
        "variant",
    };

    public static string? ValidateIssue(JsonElement? agentConfig)
    {
        if (!agentConfig.HasValue || agentConfig.Value.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in agentConfig.Value.EnumerateObject())
        {
            if (!IssueAllowedKeys.Contains(property.Name))
                return property.Name == "runtime"
                    ? "agentConfig.runtime is not supported for Issue configuration; configure runtime on the Agent definition."
                    : $"agentConfig.{property.Name} is not allowed; Issue agent config accepts only {string.Join(", ", IssueAllowedKeys)}.";
        }

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

            if (property.Name is "model" or "reasoningEffort" or "variant"
                && property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return $"agentConfig.{property.Name} must be a string or null.";
            }

            if (property.Name is "model" or "reasoningEffort" or "variant"
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
    /// The <c>provider/model</c> reference form shared by Agent definitions
    /// (the Readiness <c>model-reference-malformed</c> gap) and the Project
    /// default execution configuration (rejected at configuration time). A
    /// null or whitespace model has no reference to check and is valid
    /// here — missing-model gaps are a Readiness concern.
    /// </summary>
    public static bool HasProviderModelForm(string? model) =>
        string.IsNullOrWhiteSpace(model)
        || model.Contains('/', StringComparison.Ordinal);

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
    /// Reasoning effort is an Agent-owned execution input. It is deliberately
    /// validated independently from the runtime-specific variant dimension;
    /// capability admission decides whether a selected runtime can execute it.
    /// </summary>
    public static string? ValidateReasoningEffort(JsonElement agentConfig)
    {
        if (agentConfig.ValueKind != JsonValueKind.Object
            || !agentConfig.TryGetProperty("reasoningEffort", out var effort)
            || effort.ValueKind == JsonValueKind.Null)
            return null;
        if (effort.ValueKind != JsonValueKind.String || !ReasoningEfforts.Contains(effort.GetString()))
            return $"agentConfig.reasoningEffort must be one of {string.Join(", ", ReasoningEfforts.All)}.";
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
