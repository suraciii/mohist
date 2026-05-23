using Microsoft.Extensions.Logging;

namespace Mohist.Server.Runner.Grains;

public class RunnerRegistryGrain : Grain, IRunnerRegistryGrain
{
    private readonly Dictionary<string, string[]> _runners = new();
    private readonly ILogger<RunnerRegistryGrain> _log;

    public RunnerRegistryGrain(ILogger<RunnerRegistryGrain> log)
    {
        _log = log;
    }

    public Task RegisterAsync(string runnerId, string[] capabilities)
    {
        _runners[runnerId] = capabilities;
        _log.LogInformation("Runner {Id} registered with [{Caps}]", runnerId, string.Join(", ", capabilities));
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
}
