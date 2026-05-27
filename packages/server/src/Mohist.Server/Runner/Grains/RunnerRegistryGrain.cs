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
        _runners[info.RunnerId] = info;
        _log.LogInformation("Runner {Id} registered with [{Caps}] and {ModelCount} coder models", info.RunnerId, string.Join(", ", info.Capabilities), info.CoderModels?.Length ?? 0);
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
