using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private async Task<AgentJobReportResult> ReportUnknownResultAsync(WorkResult result)
    {
        // A restarted Runner proves only that this physical dispatch was
        // fenced locally without a durable result. Preserve that fact as
        // Unknown; never route it through terminal failure.
        var reason = string.IsNullOrWhiteSpace(result.Message)
            ? AgentJobFailureReasons.RunnerUnavailable
            : result.Message;
        await EnterUnknownStateAsync(reason);
        return new AgentJobReportResult(true, "unknown");
    }
}
