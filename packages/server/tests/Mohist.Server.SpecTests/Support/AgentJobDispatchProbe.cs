using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.SpecTests.Support;

public sealed class AgentJobDispatchProbe : IAgentJobDispatchObserver
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentJobDispatchAssignment>> _accepted =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _prepared =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _preparedCounts =
        new(StringComparer.Ordinal);

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId)
    {
        _preparedCounts.AddOrUpdate(agentJobId, 1, (_, count) => count + 1);
        PreparedSignal(agentJobId).TrySetResult();
        return Task.CompletedTask;
    }

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId)
    {
        Signal(agentJobId).TrySetResult(new AgentJobDispatchAssignment(runnerId, workId));
        return Task.CompletedTask;
    }

    public Task<AgentJobDispatchAssignment> WaitForRunnerAcceptedAsync(string agentJobId) =>
        Signal(agentJobId).Task;

    public Task WaitForAssignmentPreparedAsync(string agentJobId) =>
        PreparedSignal(agentJobId).Task;

    public int PreparedCount(string agentJobId) =>
        _preparedCounts.GetValueOrDefault(agentJobId);

    private TaskCompletionSource<AgentJobDispatchAssignment> Signal(string agentJobId) =>
        _accepted.GetOrAdd(agentJobId, _ => new(
            TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource PreparedSignal(string agentJobId) =>
        _prepared.GetOrAdd(agentJobId, _ => new(
            TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed record AgentJobDispatchAssignment(string RunnerId, string WorkId);
