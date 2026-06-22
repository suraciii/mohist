namespace Mohist.Server.Infrastructure.Orleans;

public static class GrainKey
{
    public static string Issue(string issueId) => issueId;
    public static string Agent(string projectId, string agentId) => $"{projectId}:{agentId}";
    public static string IssueCounter(string projectId) => projectId;
    public static string EpicCounter(string projectId) => projectId;
    public static string WorkflowBacklog(string projectId) => projectId;

    [Obsolete("Runner registries are global only; use RunnerRegistryKeys.Global.", error: false)]
    public static string RunnerRegistry(string projectId) => projectId;
}
