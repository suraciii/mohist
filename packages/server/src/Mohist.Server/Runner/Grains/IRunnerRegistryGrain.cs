namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistryGrain : IGrainWithStringKey
{
    Task RegisterAsync(string runnerId, string[] capabilities);
    Task UnregisterAsync(string runnerId);
    Task<IReadOnlyList<string>> ListRunnerIdsAsync();
    Task<string?> FindIdleRunnerAsync(string[] capabilities);
}

public static class RunnerRegistryKeys
{
    public const string Key = "default";
}
