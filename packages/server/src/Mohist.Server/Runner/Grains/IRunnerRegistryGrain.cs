namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistryGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync(string runnerId);
    Task<IReadOnlyList<string>> ListRunnerIdsAsync();
    Task<IReadOnlyList<string>> ListCoderModelsAsync();
}

public static class RunnerRegistryKeys
{
    public static string ForProject(string projectId) => projectId;
}
