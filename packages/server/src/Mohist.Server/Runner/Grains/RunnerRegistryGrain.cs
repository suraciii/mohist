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

    public async Task<string?> FindIdleRunnerAsync(string[] capabilities)
    {
        foreach (var (id, caps) in _runners)
        {
            if (!CanRun(caps, capabilities))
                continue;

            var runner = GrainFactory.GetGrain<IRunnerGrain>(id);
            if (await runner.IsAvailableAsync())
                return id;
        }

        return null;
    }

    private static bool CanRun(string[] runnerCapabilities, string[] requiredCapabilities)
    {
        if (requiredCapabilities.Length == 0) return true;

        return requiredCapabilities.All(required => runnerCapabilities.Any(capability => Matches(capability, required)));
    }

    private static bool Matches(string capability, string required)
    {
        if (capability == required) return true;
        if (!capability.EndsWith("/*", StringComparison.Ordinal)) return false;

        var prefix = capability[..^1];
        return required.StartsWith(prefix, StringComparison.Ordinal);
    }
}
