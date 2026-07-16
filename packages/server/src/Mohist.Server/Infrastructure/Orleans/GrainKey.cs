namespace Mohist.Server.Infrastructure.Orleans;

public static class GrainKey
{
    public static string Issue(IssueKey key) =>
        ScopedGrainKeyCodec.Format(key.ProjectId, key.IssueNumber);

    public static string Epic(EpicKey key) =>
        ScopedGrainKeyCodec.Format(key.ProjectId, key.EpicNumber);

    public static string Agent(string projectId, string agentId) => $"{projectId}:{agentId}";
    public static string IssueCounter(string projectId) => projectId;
    public static string EpicCounter(string projectId) => projectId;
    public static string WorkflowBacklog(string projectId) => projectId;

    // Private transition seam: removed in T-004 when every IssueGrain call
    // site routes through the Project-scoped IssueKey.
    internal static string Issue(string issueId) => issueId;

    [Obsolete("Runner registries are global only; use RunnerRegistryKeys.Global.", error: false)]
    public static string RunnerRegistry(string projectId) => projectId;
}
