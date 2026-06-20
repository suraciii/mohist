using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Orleans;

namespace Mohist.Server.Runner.Grains;

public class RunnerRegistryGrain : Grain, IRunnerRegistryGrain
{
    private readonly Dictionary<string, RunnerInfo> _runners = new();
    private readonly ILogger<RunnerRegistryGrain> _log;

    public RunnerRegistryGrain(ILogger<RunnerRegistryGrain> log)
    {
        _log = log;
    }

    public Task RegisterAsync(RunnerInfo info)
    {
        var isNew = !_runners.ContainsKey(info.RunnerId);
        var enriched = info with { RegisteredAt = info.RegisteredAt ?? DateTimeOffset.UtcNow };
        _runners[info.RunnerId] = enriched;
        if (isNew)
        {
            _log.LogInformation(
                "Runner {Id} registered with [{Caps}], {ModelCount} coder models, and {Slots} workflow slots",
                info.RunnerId,
                string.Join(", ", info.Capabilities),
                info.CoderModels?.Length ?? 0,
                info.MaxWorkflowSlots);
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
        var models = _runners.Values
            .SelectMany(r => r.CoderModels ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(models);
    }

    public Task<IReadOnlyDictionary<string, string[]>> ListCoderModelVariantsAsync()
    {
        var aggregated = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in _runners.Values)
        {
            if (runner.CoderModelVariants is null || runner.CoderModelVariants.Count == 0)
                continue;

            foreach (var entry in runner.CoderModelVariants)
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

    public Task<IReadOnlyList<RunnerInfo>> ListAllAsync()
    {
        return Task.FromResult<IReadOnlyList<RunnerInfo>>(_runners.Values.ToList());
    }

    public async Task<IReadOnlyList<RunnerInfo>> ListEligibleRunnersAsync(string projectId)
    {
        var localRunners = _runners.Values.ToList();
        var currentKey = this.GetPrimaryKeyString();

        List<RunnerInfo> otherRunners = new();
        if (currentKey == RunnerRegistryKeys.Global)
        {
            var projectRegistry = GrainFactory.GetGrain<IRunnerRegistryGrain>(GrainKey.RunnerRegistry(projectId));
            otherRunners = (await projectRegistry.ListAllAsync()).ToList();
        }
        else
        {
            var globalRegistry = GrainFactory.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
            otherRunners = (await globalRegistry.ListAllAsync()).ToList();
        }

        var allRunners = localRunners
            .Concat(otherRunners)
            .GroupBy(r => r.RunnerId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var eligible = allRunners
            .Where(r => string.IsNullOrWhiteSpace(r.ProjectId)
                || string.Equals(r.ProjectId, projectId, StringComparison.Ordinal))
            .ToList();

        return eligible;
    }
}
