namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistryGrain : IGrainWithGuidKey
{
    Task<string?> FindIdleRunnerAsync(string? uses);
    Task RegisterAsync(string runnerId, string[] capabilities);
    Task UnregisterAsync(string runnerId);
    Task HeartbeatAsync(string runnerId);
}
