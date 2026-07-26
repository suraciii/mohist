namespace Mohist.Server.Runner.Grains;

public interface IAgentJobWorkCoordinator
{
    Task<bool> IsWorkRunnableAsync(string agentJobId, string runnerId, string workId);
    Task<AgentJobWorkReportResult> ReportAsync(string agentJobId, string runnerId, string workId, WorkResult result);
    Task FailAsync(string agentJobId, string reason, string? agentId = null);
}

public sealed record AgentJobWorkReportResult(bool Accepted, string? Reason = null);
