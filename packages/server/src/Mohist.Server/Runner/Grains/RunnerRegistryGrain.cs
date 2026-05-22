namespace Mohist.Server.Runner.Grains;

public class RunnerRegistryGrain : Grain, IRunnerRegistryGrain
{
    private readonly Dictionary<string, RunnerEntry> _runners = new();
    private readonly ILogger<RunnerRegistryGrain> _log;

    public RunnerRegistryGrain(ILogger<RunnerRegistryGrain> log)
    {
        _log = log;
    }

    public Task RegisterAsync(string runnerId, string[] capabilities)
    {
        _runners[runnerId] = new RunnerEntry(runnerId, capabilities, DateTime.UtcNow);
        _log.LogInformation("Runner {Id} registered in registry with [{Caps}]", runnerId, string.Join(", ", capabilities));
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(string runnerId)
    {
        _runners.Remove(runnerId);
        _log.LogInformation("Runner {Id} removed from registry", runnerId);
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync(string runnerId)
    {
        if (_runners.TryGetValue(runnerId, out var entry))
            entry.LastHeartbeat = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public async Task<string?> FindIdleRunnerAsync(string? uses)
    {
        foreach (var (id, entry) in _runners)
        {
            if (uses is not null && !entry.Capabilities.Contains(uses))
                continue;

            var grain = GrainFactory.GetGrain<IRunnerGrain>(id);
            if (await grain.IsAvailableAsync())
                return id;
        }

        return null;
    }

    private record RunnerEntry(string RunnerId, string[] Capabilities, DateTime RegisteredAt)
    {
        public DateTime LastHeartbeat { get; set; } = RegisteredAt;
    }
}
