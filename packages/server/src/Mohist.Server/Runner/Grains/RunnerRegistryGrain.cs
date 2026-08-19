using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure;

namespace Mohist.Server.Runner.Grains;

public class RunnerRegistryGrain : Grain, IRunnerRegistryGrain
{
    private readonly Dictionary<string, RunnerInfo> _runners = new();
    private readonly ILogger<RunnerRegistryGrain> _log;
    private readonly TimeProvider _timeProvider;

    public RunnerRegistryGrain(ILogger<RunnerRegistryGrain> log, TimeProvider timeProvider)
    {
        _log = log;
        _timeProvider = timeProvider;
    }

    public Task RegisterAsync(RunnerInfo info)
    {
        var isNew = !_runners.ContainsKey(info.RunnerId);
        var enriched = info with { RegisteredAt = info.RegisteredAt ?? _timeProvider.GetUtcNow() };
        _runners[info.RunnerId] = enriched;
        if (isNew)
        {
            _log.LogInformation(
                "Runner {Id} registered with [{Caps}] and {ModelCount} coder models",
                info.RunnerId,
                string.Join(", ", info.Capabilities),
                info.CoderModels?.Length ?? 0);
        }
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string runnerId)
    {
        _runners.Remove(runnerId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListRunnerIdsAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_runners.Keys.ToList());
    }

    public Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync()
    {
        return Task.FromResult<IReadOnlyList<RunnerInfo>>(_runners.Values.ToList());
    }

    public Task<IReadOnlyList<string>> ListCoderModelsAsync()
    {
        return ListCoderModelsByRuntimeAsync(AgentConfigSchema.OpenCodeRuntime);
    }

    public Task<IReadOnlyList<string>> ListCoderModelsByRuntimeAsync(string runtime)
    {
        var models = _runners.Values
            .SelectMany(r => CatalogFor(r, runtime)?.Models ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(models);
    }

    public Task<IReadOnlyDictionary<string, string[]>> ListCoderModelVariantsAsync()
    {
        return ListCoderModelVariantsByRuntimeAsync(AgentConfigSchema.OpenCodeRuntime);
    }

    public Task<IReadOnlyDictionary<string, string[]>> ListCoderModelVariantsByRuntimeAsync(string runtime)
        => ListModelValuesByRuntimeAsync(runtime, catalog => catalog.Variants);

    public Task<IReadOnlyDictionary<string, string[]>> ListCoderReasoningEffortsByRuntimeAsync(string runtime)
        => ListModelValuesByRuntimeAsync(runtime, catalog => catalog.ReasoningEfforts);

    private Task<IReadOnlyDictionary<string, string[]>> ListModelValuesByRuntimeAsync(
        string runtime,
        Func<RuntimeCatalogEntry, Dictionary<string, string[]>?> selectValues)
    {
        var aggregated = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in _runners.Values)
        {
            var catalog = CatalogFor(runner, runtime);
            var values = catalog is null ? null : selectValues(catalog);
            if (values is null || values.Count == 0)
                continue;

            foreach (var entry in values)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                var reportedVariants = (entry.Value ?? [])
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (reportedVariants.Length == 0)
                    continue;

                if (aggregated.TryGetValue(entry.Key, out var existing))
                {
                    var union = existing
                        .Concat(reportedVariants)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    aggregated[entry.Key] = union;
                }
                else
                {
                    aggregated[entry.Key] = reportedVariants;
                }
            }
        }

        var materialized = aggregated
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, string[]>>(materialized);
    }

    private static RuntimeCatalogEntry? CatalogFor(RunnerInfo runner, string runtime)
    {
        if (runner.RuntimeCatalogs is not null)
        {
            foreach (var entry in runner.RuntimeCatalogs)
            {
                if (string.Equals(entry.Key, runtime, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }
        }

        if (!string.Equals(runtime, AgentConfigSchema.OpenCodeRuntime, StringComparison.OrdinalIgnoreCase))
            return null;
        if (runner.CoderModels is null && runner.CoderModelVariants is null)
            return null;
        return new RuntimeCatalogEntry(runner.CoderModels ?? [], runner.CoderModelVariants);
    }

    public Task<IReadOnlyList<RunnerInfo>> ListAllAsync()
    {
        return Task.FromResult<IReadOnlyList<RunnerInfo>>(_runners.Values.ToList());
    }

    public Task<IReadOnlyList<RunnerInfo>> ListEligibleRunnersAsync(string projectId)
    {
        return Task.FromResult<IReadOnlyList<RunnerInfo>>(_runners.Values.ToList());
    }
}
