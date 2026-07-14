using Mohist.Server.Agent.Grains;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Infrastructure.Hosting;

public sealed class AgentJobWorkCoordinator : IAgentJobWorkCoordinator, ISingletonService
{
    private readonly IGrainFactory _grains;

    public AgentJobWorkCoordinator(IGrainFactory grains)
    {
        _grains = grains;
    }

    public Task<bool> IsWorkRunnableAsync(string agentJobId, string runnerId, string workId) =>
        _grains.GetGrain<IAgentJobGrain>(agentJobId).IsWorkRunnableAsync(runnerId, workId);

    public async Task<AgentJobWorkReportResult> ReportAsync(
        string agentJobId,
        string runnerId,
        string workId,
        WorkResult result)
    {
        var report = await _grains.GetGrain<IAgentJobGrain>(agentJobId).ReportResultAsync(runnerId, workId, result);
        return new AgentJobWorkReportResult(report.Accepted, report.Reason);
    }

    public Task FailAsync(string agentJobId, string reason) =>
        _grains.GetGrain<IAgentJobGrain>(agentJobId).FailAsync(reason);
}
