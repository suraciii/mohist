using Mohist.Server.Runner.Grains;
using Orleans;

namespace Mohist.Server.Agent.Services;

/// <summary>
/// The immutable execution tuple evaluated by the capability resolver.
/// Null effort and variant values mean that the corresponding configuration
/// member is unset; the values are otherwise preserved exactly as supplied.
/// </summary>
[GenerateSerializer]
public sealed record AgentExecutionCapabilityTuple(
    [property: Id(0)] string Runtime,
    [property: Id(1)] string? Model,
    [property: Id(2)] string? ReasoningEffort,
    [property: Id(3)] string? Variant);

/// <summary>
/// The catalog and readiness witness for one runner at one point in time.
/// The resolver only reads this value. Admission owns obtaining a fresh
/// snapshot and fencing a later claim against it.
/// </summary>
public sealed record AgentExecutionCapabilitySnapshot(
    string RunnerId,
    string Runtime,
    RuntimeCatalogEntry? Catalog,
    bool RuntimeReady = true,
    long? RuntimeGeneration = null,
    string? ConnectionGeneration = null);

public enum AgentExecutionCapabilityDisposition
{
    Supported,
    NeedsSetup,
    Unavailable,
    UnsupportedExecutionConfiguration,
    IncompatibleExecutionConfiguration,
}

/// <summary>
/// Evidence retained for a non-supported resolution. The tuple is always the
/// frozen input, never a value reconstructed from the catalog.
/// </summary>
[GenerateSerializer]
public sealed record AgentExecutionCapabilityFailureEvidence(
    [property: Id(0)] AgentExecutionCapabilityTuple FrozenTuple,
    [property: Id(1)] string? RunnerId,
    [property: Id(2)] string? CapabilityRevision);

public sealed record AgentExecutionCapabilityResolution(
    AgentExecutionCapabilityDisposition Disposition,
    AgentExecutionCapabilityTuple FrozenTuple,
    string? RunnerId = null,
    string? CapabilityRevision = null,
    AgentExecutionCapabilityFailureEvidence? FailureEvidence = null)
{
    public AgentExecutionCapabilityTuple Tuple => FrozenTuple;

    public string DispositionCode => Disposition switch
    {
        AgentExecutionCapabilityDisposition.Supported => "supported",
        AgentExecutionCapabilityDisposition.NeedsSetup => "needs-setup",
        AgentExecutionCapabilityDisposition.Unavailable => "unavailable",
        AgentExecutionCapabilityDisposition.UnsupportedExecutionConfiguration => "unsupported_execution_configuration",
        AgentExecutionCapabilityDisposition.IncompatibleExecutionConfiguration => "incompatible_execution_configuration",
        _ => throw new ArgumentOutOfRangeException(),
    };

    public bool IsSupported => Disposition == AgentExecutionCapabilityDisposition.Supported;
    public bool IsPending => Disposition is
        AgentExecutionCapabilityDisposition.NeedsSetup or
        AgentExecutionCapabilityDisposition.Unavailable;
}

/// <summary>
/// Pure capability decision table for an immutable Agent execution tuple.
/// This type deliberately has no grain, service, or execution dependencies.
/// </summary>
public static class AgentExecutionCapabilityResolver
{
    public static AgentExecutionCapabilityResolution Resolve(
        string runtime,
        string? model,
        string? reasoningEffort,
        string? variant,
        IReadOnlyList<AgentExecutionCapabilitySnapshot>? catalogSnapshot)
    {
        var tuple = new AgentExecutionCapabilityTuple(runtime, model, reasoningEffort, variant);
        var candidates = (catalogSnapshot ?? [])
            .Where(snapshot => string.Equals(snapshot.Runtime, runtime, StringComparison.OrdinalIgnoreCase))
            .OrderBy(snapshot => snapshot.RunnerId, StringComparer.Ordinal)
            .ToArray();

        if (candidates.Length == 0)
            return Pending(tuple, AgentExecutionCapabilityDisposition.NeedsSetup);

        var ready = candidates.Where(snapshot => snapshot.RuntimeReady).ToArray();
        if (ready.Length == 0)
            return Pending(
                tuple,
                AgentExecutionCapabilityDisposition.Unavailable,
                candidates[0].RunnerId);

        var authoritative = ready
            .Where(snapshot => IsAuthoritative(snapshot.Catalog))
            .ToArray();
        if (authoritative.Length == 0)
            return Pending(tuple, AgentExecutionCapabilityDisposition.NeedsSetup, ready[0].RunnerId);

        var supported = authoritative.FirstOrDefault(snapshot => IsCompatible(snapshot.Catalog!, tuple));
        if (supported is not null)
        {
            return new AgentExecutionCapabilityResolution(
                AgentExecutionCapabilityDisposition.Supported,
                tuple,
                supported.RunnerId,
                supported.Catalog!.CapabilityRevision);
        }

        // Explicit runtime-level rejection has precedence over a missing tuple
        // member. This keeps an effort on a variant-only runtime a stable
        // unsupported-configuration failure even if the model is also absent.
        var unsupported = authoritative.FirstOrDefault(snapshot =>
            tuple.ReasoningEffort is not null
            && snapshot.Catalog!.SupportsReasoningEffort == false);
        if (unsupported is not null)
        {
            return Rejected(
                tuple,
                AgentExecutionCapabilityDisposition.UnsupportedExecutionConfiguration,
                unsupported);
        }

        var incompatible = authoritative[0];
        return Rejected(
            tuple,
            AgentExecutionCapabilityDisposition.IncompatibleExecutionConfiguration,
            incompatible);
    }

    public static AgentExecutionCapabilityResolution Resolve(
        AgentExecutionCapabilityTuple tuple,
        IReadOnlyList<AgentExecutionCapabilitySnapshot>? catalogSnapshot) =>
        Resolve(
            tuple.Runtime,
            tuple.Model,
            tuple.ReasoningEffort,
            tuple.Variant,
            catalogSnapshot);

    private static bool IsAuthoritative(RuntimeCatalogEntry? catalog) =>
        catalog is not null
        && catalog.Complete == true
        && !string.IsNullOrWhiteSpace(catalog.CapabilityRevision);

    private static bool IsCompatible(
        RuntimeCatalogEntry catalog,
        AgentExecutionCapabilityTuple tuple)
    {
        // The catalog is a configuration hint, not the execution
        // authority (`design/runtimes/opencode.md` — the model catalog
        // assists configuration; the runtime validates the model at
        // execution time). An unset model means the runtime uses its
        // default; an empty catalog model list means the catalog has no
        // model discovery and the runtime validates any supplied model
        // at execution time. Either case is `supported` rather than
        // `incompatible_execution_configuration`. A catalog that lists
        // explicit models still rejects any model that is not on the
        // list as a deterministic incompatibility.
        if (tuple.Model is not null)
        {
            if (catalog.Models is { Length: > 0 }
                && !Contains(catalog.Models, tuple.Model, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (tuple.ReasoningEffort is not null)
        {
            // The catalog's explicit `supportsReasoningEffort` flag is
            // the source of truth for effort support. A non-true value
            // (false or null) means the runtime does not support effort
            // — explicit rejection per the design contract.
            if (catalog.SupportsReasoningEffort != true)
                return false;
        }

        // Variant validation is only enforced when the catalog has
        // explicit per-model variant definitions. An empty variant map
        // means the runtime validates the variant at execution time,
        // matching the model-validation rule above.
        if (tuple.Variant is not null
            && catalog.Variants is { Count: > 0 }
            && !ContainsForModel(catalog.Variants, tuple.Model, tuple.Variant))
            return false;

        return true;
    }

    private static bool ContainsForModel(
        Dictionary<string, string[]>? valuesByModel,
        string? model,
        string value)
    {
        if (valuesByModel is null || model is null)
            return false;

        var values = valuesByModel.FirstOrDefault(entry =>
            string.Equals(entry.Key, model, StringComparison.OrdinalIgnoreCase)).Value;
        return values is not null
            && Contains(values, value, StringComparison.Ordinal);
    }

    private static bool Contains(
        IEnumerable<string>? values,
        string value,
        StringComparison comparison)
    {
        return values is not null
            && values.Any(candidate => string.Equals(candidate, value, comparison));
    }

    private static AgentExecutionCapabilityResolution Pending(
        AgentExecutionCapabilityTuple tuple,
        AgentExecutionCapabilityDisposition disposition,
        string? runnerId = null)
    {
        return new AgentExecutionCapabilityResolution(
            disposition,
            tuple,
            runnerId,
            FailureEvidence: new AgentExecutionCapabilityFailureEvidence(tuple, runnerId, null));
    }

    private static AgentExecutionCapabilityResolution Rejected(
        AgentExecutionCapabilityTuple tuple,
        AgentExecutionCapabilityDisposition disposition,
        AgentExecutionCapabilitySnapshot snapshot)
    {
        var revision = snapshot.Catalog!.CapabilityRevision;
        return new AgentExecutionCapabilityResolution(
            disposition,
            tuple,
            snapshot.RunnerId,
            revision,
            new AgentExecutionCapabilityFailureEvidence(tuple, snapshot.RunnerId, revision));
    }
}
