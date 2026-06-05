namespace Mohist.Server.Infrastructure.Orleans;

public static class GrainKey
{
    public static string Issue(string issueId) => issueId;
    public static string IssueCounter(string projectId) => projectId;
    public static string EpicCounter(string projectId) => projectId;
    public static string WorkflowBacklog(string projectId) => projectId;
    public static string RunnerRegistry(string projectId) => projectId;
    public static string AgentSession(string projectId, string workflowRunId, string sessionName) => $"{projectId}/{workflowRunId}/{sessionName}";
}
