namespace Mohist.Server.Infrastructure.Data.AgentJobs;

public interface IAgentJobReportPersistenceFailureInjector
{
    void BeforePersist(string agentJobId, string workId);

    void BeforeActivitySettlementReminder(string agentJobId) { }

    void BeforeActivitySettlementPersist(string agentJobId) { }
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

    public void BeforeActivitySettlementReminder(string agentJobId)
    {
    }

    public void BeforeActivitySettlementPersist(string agentJobId)
    {
    }
}
