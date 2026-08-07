using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Counting probe for the best-effort AgentJob dispatch-observer side
/// channel. Tests must converge on the authoritative
/// <see cref="IAgentJobGrain"/> runtime snapshot
/// (<see cref="AgentJobConvergence"/>) rather than await these signals:
/// the observer is a NoOp in production and can silently drop signals
/// under load. Retained only for <see cref="PreparedCount"/> assertions.
/// </summary>
public sealed class AgentJobDispatchProbe : IAgentJobDispatchObserver
{
    private readonly ConcurrentDictionary<string, int> _preparedCounts =
        new(StringComparer.Ordinal);

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _preparedCounts.AddOrUpdate(agentJobId, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId) =>
        Task.CompletedTask;

    public int PreparedCount(string agentJobId) =>
        _preparedCounts.GetValueOrDefault(agentJobId);
}
