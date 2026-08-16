namespace Mohist.Server.Runner.Grains;

/// <summary>
/// Pure capability-claim matching against a runner's registered runtime
/// catalogs and readiness witnesses. Extracted so the grain only orchestrates;
/// the predicate is deterministic and unit-testable without a silo.
/// </summary>
internal static class RunnerCapabilityGate
{
    public static bool Matches(
        RunnerInfo? info,
        string? readinessConnectionGeneration,
        IReadOnlyDictionary<string, RuntimeReadinessWitness> readiness,
        CapabilityClaimExpectation expectation)
    {
        if (info is null
            || !string.Equals(expectation.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(expectation.OwnerId)
            || string.IsNullOrWhiteSpace(expectation.WorkId))
            return false;

        if (expectation.ConnectionGeneration is not null
            && !string.Equals(info.ConnectionGeneration, expectation.ConnectionGeneration, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(expectation.Runtime))
            return false;

        var catalog = RuntimeCatalogFor(info, expectation.Runtime);
        var requiresCapabilityRevision = !string.IsNullOrWhiteSpace(expectation.ReasoningEffort);
        if (requiresCapabilityRevision
            && (catalog?.SupportsReasoningEffort != true
                || catalog.Complete != true
                || string.IsNullOrWhiteSpace(catalog.CapabilityRevision)
                || !string.Equals(catalog.CapabilityRevision, expectation.CapabilityRevision, StringComparison.Ordinal)))
            return false;

        if (expectation.CapabilityRevision is not null
            && !string.Equals(catalog?.CapabilityRevision, expectation.CapabilityRevision, StringComparison.Ordinal))
            return false;

        if (catalog is not null && !Contains(catalog.Models, expectation.Model))
            return false;
        if (expectation.Variant is not null
            && !Contains(catalog?.Variants, expectation.Model, expectation.Variant))
            return false;
        if (expectation.ReasoningEffort is not null
            && !Contains(catalog?.Variants, expectation.Model, expectation.ReasoningEffort))
            return false;

        if (expectation.ReasoningEffort is null)
            return true;

        if (expectation.RuntimeGeneration is not > 0
            || string.IsNullOrWhiteSpace(expectation.ConnectionGeneration)
            || !string.Equals(readinessConnectionGeneration, expectation.ConnectionGeneration, StringComparison.Ordinal))
            return false;

        return readiness.TryGetValue(expectation.Runtime, out var witness)
            && witness.Ready
            && witness.Generation == expectation.RuntimeGeneration;
    }

    public static RuntimeCatalogEntry? RuntimeCatalogFor(RunnerInfo info, string runtime)
    {
        if (info.RuntimeCatalogs is null)
            return null;

        foreach (var entry in info.RuntimeCatalogs)
        {
            if (string.Equals(entry.Key, runtime, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return null;
    }

    private static bool Contains(string[]? values, string? expected) =>
        expected is null || (values?.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static bool Contains(
        Dictionary<string, string[]>? values,
        string? model,
        string expected)
    {
        if (model is null || values is null)
            return false;

        return values.TryGetValue(model, out var supported)
            && supported.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
    }
}
