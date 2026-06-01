using Microsoft.Extensions.Logging;

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
        _runners[info.RunnerId] = info;
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
}
