namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistry
{
    void Register(string runnerId, string[] capabilities);
    void Unregister(string runnerId);
    Task<string?> FindIdleRunnerAsync(IGrainFactory grainFactory, string? uses);
}

public class RunnerRegistry : IRunnerRegistry
{
    private readonly Dictionary<string, string[]> _runners = new();
    private readonly ILogger<RunnerRegistry> _log;

    public RunnerRegistry(ILogger<RunnerRegistry> log)
    {
        _log = log;
    }

    public void Register(string runnerId, string[] capabilities)
    {
        _runners[runnerId] = capabilities;
        _log.LogInformation("Runner {Id} registered with [{Caps}]", runnerId, string.Join(", ", capabilities));
    }

    public void Unregister(string runnerId)
    {
        _runners.Remove(runnerId);
    }

    public async Task<string?> FindIdleRunnerAsync(IGrainFactory grainFactory, string? uses)
    {
        foreach (var (id, caps) in _runners)
        {
            if (uses is not null && !caps.Contains(uses))
                continue;

            var grain = grainFactory.GetGrain<IRunnerGrain>(id);
            if (await grain.IsAvailableAsync())
                return id;
        }

        return null;
    }
}
