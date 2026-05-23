namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistryGrain : IGrainWithStringKey
{
    Task RegisterAsync(string runnerId, string[] capabilities);
    Task UnregisterAsync(string runnerId);
    Task<IReadOnlyList<string>> ListRunnerIdsAsync();
}

public static class RunnerRegistryKeys
{
    public const string Key = "default";
}
