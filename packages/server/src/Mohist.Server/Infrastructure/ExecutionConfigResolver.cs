using System.Text.Json;

namespace Mohist.Server.Infrastructure;

/// <summary>
/// One nullable execution-field selection (Runtime, Model, Variant) shared by
/// the three sources of the single precedence rule: the caller-supplied hint,
/// the Agent definition, and the Project default. Whitespace values are
/// treated as absent; an explicitly malformed value (e.g. a model without the
/// <c>provider/model</c> form) is preserved as-is — masking is rejected at
/// the value's entry point (hint validation,
/// <see cref="AgentConfigSchema"/> on definitions and defaults), never by
/// substituting a lower-precedence source.
/// </summary>
[GenerateSerializer]
public sealed record ExecutionConfigHint(
    [property: Id(0)] string? Runtime = null,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? Variant = null);

/// <summary>
/// Resolved execution configuration: every field settled by the precedence
/// rule, with <see cref="Runtime"/> never null (it defaults to
/// <see cref="AgentConfigSchema.OpenCodeRuntime"/> when no source supplies
/// one). Model and Variant stay null when no source supplies them.
/// </summary>
[GenerateSerializer]
public sealed record ResolvedExecutionConfig(
    [property: Id(0)] string Runtime,
    [property: Id(1)] string? Model,
    [property: Id(2)] string? Variant);

/// <summary>
/// The one precedence rule for every execution field: caller hint, then
/// Agent definition, then Project default — applied per field so a default
/// can fill a definition gap (e.g. supply the Model when the definition
/// carries only a Variant) without overriding a definition value. Used at
/// Readiness evaluation and at launch-time resolution so an Agent that
/// Readiness reports launchable dispatches with the model Readiness
/// resolved.
/// </summary>
public static class ExecutionConfigResolver
{
    public static ResolvedExecutionConfig Resolve(
        ExecutionConfigHint? callerHint,
        ExecutionConfigHint? definition,
        ExecutionConfigHint? projectDefault)
    {
        var runtime = FirstSupplied(
            callerHint?.Runtime,
            definition?.Runtime,
            projectDefault?.Runtime)
            ?? AgentConfigSchema.OpenCodeRuntime;
        return new ResolvedExecutionConfig(
            runtime,
            FirstSupplied(callerHint?.Model, definition?.Model, projectDefault?.Model),
            FirstSupplied(callerHint?.Variant, definition?.Variant, projectDefault?.Variant));
    }

    /// <summary>
    /// Raw per-field extraction from an Agent definition's
    /// <c>agentConfig</c>. Unlike the launch-side snapshot helpers, the
    /// runtime is read verbatim (no <c>opencode</c> fallback — that is the
    /// resolver's job) and a Variant set without a Model survives so the
    /// precedence rule can fill the missing Model from the Project default.
    /// </summary>
    public static ExecutionConfigHint? FromAgentConfig(JsonElement? agentConfig)
    {
        if (agentConfig is not { ValueKind: JsonValueKind.Object } config)
            return null;

        var hint = new ExecutionConfigHint(
            TryReadString(config, "runtime"),
            TryReadString(config, "model"),
            TryReadString(config, "variant"));
        return hint.Runtime is null && hint.Model is null && hint.Variant is null
            ? null
            : hint;
    }

    private static string? FirstSupplied(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? TryReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String)
            return null;
        var raw = value.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}

/// <summary>
/// Storage codec for the persisted execution-config selections (the Project
/// default on <c>ProjectRow.DefaultExecutionConfigJson</c>). Writes only the
/// supplied fields and reads absent/blank storage as "unset".
/// </summary>
public static class ExecutionConfigJson
{
    public static string? Serialize(ExecutionConfigHint? config)
    {
        if (config is null) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(config.Runtime)) values["runtime"] = config.Runtime!;
        if (!string.IsNullOrWhiteSpace(config.Model)) values["model"] = config.Model!;
        if (!string.IsNullOrWhiteSpace(config.Variant)) values["variant"] = config.Variant!;
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, JSON.Options);
    }

    public static ExecutionConfigHint? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            return new ExecutionConfigHint(
                ReadString(root, "runtime"),
                ReadString(root, "model"),
                ReadString(root, "variant"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = value.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }
}
