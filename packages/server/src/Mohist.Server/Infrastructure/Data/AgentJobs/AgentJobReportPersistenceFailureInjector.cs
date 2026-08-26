namespace Mohist.Server.Infrastructure.Data.AgentJobs;

public interface IAgentJobReportPersistenceFailureInjector
{
    void BeforePersist(string agentJobId, string workId);
}

public sealed class NoopAgentJobReportPersistenceFailureInjector : IAgentJobReportPersistenceFailureInjector
{
    public static NoopAgentJobReportPersistenceFailureInjector Instance { get; } = new();

    private NoopAgentJobReportPersistenceFailureInjector()
    {
    }

    public void BeforePersist(string agentJobId, string workId)
    {
    }
}
