using System.Text.Json;

namespace Mohist.Server.Infrastructure;

/// <summary>
/// One nullable execution-field selection (Runtime, Model, Reasoning Effort,
/// Variant) shared by the two sources of the single precedence rule: the
/// caller-supplied hint and the Agent definition. Whitespace values are
/// treated as absent; an explicitly malformed value (e.g. a model without the
/// <c>provider/model</c> form) is preserved as-is — masking is rejected at
/// the value's entry point (hint validation,
/// <see cref="AgentConfigSchema"/> on definitions), never by substituting a
/// lower-precedence source.
/// </summary>
[GenerateSerializer]
public sealed record ExecutionConfigHint(
    [property: Id(0)] string? Runtime = null,
    [property: Id(1)] string? Model = null,
    [property: Id(2)] string? Variant = null,
    [property: Id(3)] string? ReasoningEffort = null);

/// <summary>
/// Resolved execution configuration: every field settled by the precedence
/// rule, with <see cref="Runtime"/> never null (it defaults to
/// <see cref="AgentConfigSchema.DefaultRuntime"/> when no source supplies
/// one). Model and Variant stay null when no source supplies them; a null
/// Model means the Runtime chooses the model at dispatch.
/// </summary>
[GenerateSerializer]
public sealed record ResolvedExecutionConfig(
    [property: Id(0)] string Runtime,
    [property: Id(1)] string? Model,
    [property: Id(2)] string? Variant);

/// <summary>
/// The one precedence rule for every execution field: caller hint, then
/// Agent definition — applied per field so the hint can override one field
/// (e.g. the Model) without supplying the rest. There is no Project term and
/// nothing is inherited from another resource. Used at Readiness evaluation
/// and at launch-time resolution so an Agent that Readiness reports
/// launchable dispatches with the configuration Readiness resolved.
/// </summary>
public static class ExecutionConfigResolver
{
    public static ResolvedExecutionConfig Resolve(
        ExecutionConfigHint? callerHint,
        ExecutionConfigHint? definition)
    {
        var runtime = FirstSupplied(
            callerHint?.Runtime,
            definition?.Runtime)
            ?? AgentConfigSchema.DefaultRuntime;
        return new ResolvedExecutionConfig(
            runtime,
            FirstSupplied(callerHint?.Model, definition?.Model),
            FirstSupplied(callerHint?.Variant, definition?.Variant));
    }

    /// <summary>
    /// Raw per-field extraction from an Agent definition's
    /// <c>agentConfig</c>. The runtime is read verbatim (no default fallback
    /// — that is the resolver's job) and a Variant set without a Model
    /// survives so the resolver preserves the explicitly configured value.
    /// Reasoning Effort is not part of resolution: it is never encoded as a
    /// Variant, so callers read it with
    /// <c>AgentLauncher.ResolveReasoningEffort</c>.
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
