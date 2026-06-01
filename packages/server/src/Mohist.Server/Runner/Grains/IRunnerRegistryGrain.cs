namespace Mohist.Server.Runner.Grains;

public interface IRunnerRegistryGrain : IGrainWithStringKey
{
    Task RegisterAsync(RunnerInfo info);
    Task UnregisterAsync(string runnerId);
    Task<IReadOnlyList<string>> ListRunnerIdsAsync();
    Task<IReadOnlyList<RunnerInfo>> ListRunnersAsync();
    Task<IReadOnlyList<string>> ListCoderModelsAsync();

    /// <summary>
    /// Returns all registered runner info entries in this registry without filtering.
    /// </summary>
    Task<IReadOnlyList<RunnerInfo>> ListAllAsync();

    /// <summary>
    /// Returns eligible runners for a project: global runners and runners scoped to the selected project.
    /// Runners scoped only to other projects are excluded.
    /// </summary>
    Task<IReadOnlyList<RunnerInfo>> ListEligibleRunnersAsync(string projectId);
}

public static class RunnerRegistryKeys
{
    public const string Global = "__global__";

    public static string ForProject(string projectId) => projectId;
}
