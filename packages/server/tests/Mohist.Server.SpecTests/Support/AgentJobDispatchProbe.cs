using System.Collections.Concurrent;
using Mohist.Server.Agent.Grains;

namespace Mohist.Server.SpecTests.Support;

public sealed class AgentJobDispatchProbe : IAgentJobDispatchObserver
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentJobDispatchAssignment>> _accepted =
        new(StringComparer.Ordinal);

    public Task AssignmentPreparedAsync(string agentJobId, string runnerId, string workId) => Task.CompletedTask;

    public Task RunnerAcceptedAsync(string agentJobId, string runnerId, string workId)
    {
        Signal(agentJobId).TrySetResult(new AgentJobDispatchAssignment(runnerId, workId));
        return Task.CompletedTask;
    }

    public Task<AgentJobDispatchAssignment> WaitForRunnerAcceptedAsync(string agentJobId) =>
        Signal(agentJobId).Task;

    private TaskCompletionSource<AgentJobDispatchAssignment> Signal(string agentJobId) =>
        _accepted.GetOrAdd(agentJobId, _ => new(
            TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed record AgentJobDispatchAssignment(string RunnerId, string WorkId);
