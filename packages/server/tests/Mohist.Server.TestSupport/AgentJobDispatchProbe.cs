using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Test-only probe for the best-effort AgentJob dispatch-observer side
/// channel. The prepared signal is emitted after the durable assignment
/// ledger write; tests may use it to order a subsequent protocol request
/// while keeping the HTTP poll as the claim assertion.
/// </summary>
public sealed class AgentJobDispatchProbe : IAgentJobDispatchObserver
{
    private readonly ConcurrentDictionary<string, int> _preparedCounts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _preparedSignals =
        new(StringComparer.Ordinal);

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _preparedCounts.AddOrUpdate(agentJobId, 1, (_, count) => count + 1);
        if (_preparedSignals.TryGetValue(agentJobId, out var signal))
            signal.TrySetResult();
        return Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId) =>
        Task.CompletedTask;

    public int PreparedCount(string agentJobId) =>
        _preparedCounts.GetValueOrDefault(agentJobId);

    public Task WaitForAssignmentPreparedAsync(string agentJobId)
    {
        if (_preparedCounts.ContainsKey(agentJobId))
            return Task.CompletedTask;

        var signal = _preparedSignals.GetOrAdd(
            agentJobId,
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_preparedCounts.ContainsKey(agentJobId))
            signal.TrySetResult();
        return signal.Task;
    }
}
