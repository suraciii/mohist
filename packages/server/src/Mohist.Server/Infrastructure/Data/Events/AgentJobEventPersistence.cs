namespace Mohist.Server.Infrastructure.Data.Events;

public static class AgentJobEventPersistence
{
    public const string SourcePrefix = "/mohist/agent-job/";
    public static string AgentJobSource(string agentJobId) => $"{SourcePrefix}{agentJobId}";
}
