using Mohist.Server.Infrastructure;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// The closed execution override accepted by the manual launch contract.
/// The <c>*Specified</c> flags preserve the distinction between omission and
/// an explicit JSON null for request fingerprints and resolution.
/// </summary>
public sealed record AgentLaunchExecutionOverride(
    bool RuntimeSpecified,
    string? Runtime,
    bool ModelSpecified,
    string? Model,
    bool VariantSpecified,
    string? Variant,
    bool ReasoningEffortSpecified,
    string? ReasoningEffort,
    string CanonicalJson)
{
    public bool HasValue => RuntimeSpecified
        || ModelSpecified
        || VariantSpecified
        || ReasoningEffortSpecified;
}

public sealed record AgentLaunchExecutionResolution(
    AgentExecutionDefinition Definition,
    IReadOnlyDictionary<string, string> Sources,
    bool HasOverride,
    bool MatchesSavedDefinition,
    string CapabilityState);

public sealed class AgentLaunchExecutionValidationException : Exception
{
    public AgentLaunchExecutionValidationException(string message)
        : base(message)
    {
    }

    public string ErrorCode => "invalid_execution_override";
}

/// <summary>
/// Pure semantic owner for launch execution overrides. It never probes a
/// Runner or provider and never falls back from a requested tuple to another
/// runtime. The eventual capability/revision gate remains a separate claim
/// concern; a non-identical override is therefore previewable but not yet
/// admitted by the current launch path.
/// </summary>
public static class AgentLaunchExecutionResolver
{
    public static AgentLaunchExecutionResolution Resolve(
        AgentExecutionDefinition saved,
        AgentLaunchExecutionOverride? executionOverride)
    {
        ArgumentNullException.ThrowIfNull(saved);

        var requested = executionOverride;
        var runtime = ResolveValue(
            saved.Runtime,
            requested?.RuntimeSpecified == true,
            requested?.Runtime) ?? AgentConfigSchema.OpenCodeRuntime;
        var model = ResolveValue(
            saved.Model,
            requested?.ModelSpecified == true,
            requested?.Model);
        var variant = ResolveValue(
            saved.Variant,
            requested?.VariantSpecified == true,
            requested?.Variant);
        var effort = ResolveValue(
            saved.ReasoningEffort,
            requested?.ReasoningEffortSpecified == true,
            requested?.ReasoningEffort);

        ValidateRuntime(requested, runtime);
        ValidateText(requested?.ModelSpecified == true, requested?.Model, "model");
        ValidateText(requested?.VariantSpecified == true, requested?.Variant, "variant");
        ValidateReasoningEffort(requested, effort);
        if (model is null && variant is not null)
        {
            throw new AgentLaunchExecutionValidationException(
                "execution.variant requires a non-null execution.model.");
        }

        var definition = saved with
        {
            Runtime = runtime,
            Model = model,
            Variant = variant,
            ReasoningEffort = effort,
        };
        var matches = string.Equals(saved.Runtime, definition.Runtime, StringComparison.Ordinal)
            && string.Equals(saved.Model, definition.Model, StringComparison.Ordinal)
            && string.Equals(saved.Variant, definition.Variant, StringComparison.Ordinal)
            && string.Equals(saved.ReasoningEffort, definition.ReasoningEffort, StringComparison.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runtime"] = requested?.RuntimeSpecified == true ? "override" : "agent",
            ["model"] = requested?.ModelSpecified == true ? "override" : "agent",
            ["variant"] = requested?.VariantSpecified == true ? "override" : "agent",
            ["reasoningEffort"] = requested?.ReasoningEffortSpecified == true ? "override" : "agent",
        };

        return new AgentLaunchExecutionResolution(
            definition,
            sources,
            requested?.HasValue == true,
            matches,
            requested?.HasValue == true && !matches ? "unknown" : "configured");
    }

    private static string? ResolveValue(string? saved, bool specified, string? requested) =>
        specified ? requested : saved;

    private static void ValidateRuntime(
        AgentLaunchExecutionOverride? requested,
        string runtime)
    {
        if (requested?.RuntimeSpecified != true)
            return;
        if (requested.Runtime is null)
            return;
        if (!AgentConfigSchema.AllowedRuntimes.Contains(runtime))
        {
            throw new AgentLaunchExecutionValidationException(
                $"execution.runtime '{runtime}' is not supported; the execution runtime accepts only {string.Join(", ", AgentConfigSchema.AllowedRuntimes)}.");
        }
    }

    private static void ValidateText(bool specified, string? value, string field)
    {
        if (specified && value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new AgentLaunchExecutionValidationException(
                $"execution.{field} must be a non-empty string or null.");
        }
    }

    private static void ValidateReasoningEffort(
        AgentLaunchExecutionOverride? requested,
        string? effort)
    {
        if (requested?.ReasoningEffortSpecified != true || effort is null)
            return;
        if (!ReasoningEfforts.Contains(effort))
        {
            throw new AgentLaunchExecutionValidationException(
                $"execution.reasoningEffort must be one of {string.Join(", ", ReasoningEfforts.All)} or null.");
        }
    }
}
