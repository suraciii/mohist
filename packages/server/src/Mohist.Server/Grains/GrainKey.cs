namespace Mohist.Server.Grains;

public static class GrainKey
{
    public static string Issue(string projectId, int number) => $"{projectId}:{number}";
    public static string IssueCounter(string projectId) => projectId;
    public static string WorkflowBacklog(string projectId) => projectId;
    public static string RunnerRegistry(string projectId) => projectId;
    public static string WorkflowAgentSession(string projectId, string workflowRunId, string sessionName) => $"{projectId}/{workflowRunId}/{sessionName}";
}
