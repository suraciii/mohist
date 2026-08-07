using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.TestSupport;

public sealed class AgentSessionPersistenceTestProbe : IAgentSessionPersistenceObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<AgentSessionPersistenceResult>> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<Waiter>> _waiters = new(StringComparer.Ordinal);
    private readonly Action? _advance;

    public AgentSessionPersistenceTestProbe(Action? advance = null)
    {
        _advance = advance;
    }

    public AgentSessionPersistenceCheckpoint Checkpoint(string sessionId)
    {
        lock (_gate)
            return new AgentSessionPersistenceCheckpoint(this, sessionId, CurrentCycle(sessionId));
    }

    public long StartCycle(string sessionId)
    {
        lock (_gate)
        {
            var cycleId = CurrentCycle(sessionId) + 1;
            _cycles[sessionId] = cycleId;
            return cycleId;
        }
    }

    public void Report(AgentSessionPersistenceResult result)
    {
        TaskCompletionSource<AgentSessionPersistenceResult>? waiter = null;
        lock (_gate)
        {
            if (_waiters.TryGetValue(result.SessionId, out var waiters))
            {
                while (waiters.Count > 0 && waiters.Peek().Completion.Task.IsCompleted)
                    waiters.Dequeue();

                if (waiters.Count > 0 && result.CycleId > waiters.Peek().MinimumCycleId)
                    waiter = waiters.Dequeue().Completion;

                if (waiters.Count == 0)
                    _waiters.Remove(result.SessionId);
            }

            if (waiter is null)
            {
                if (!_results.TryGetValue(result.SessionId, out var results))
                    _results[result.SessionId] = results = new Queue<AgentSessionPersistenceResult>();
                results.Enqueue(result);
            }
        }

        waiter?.SetResult(result);
    }

    public Task<AgentSessionPersistenceResult> WaitForNextAsync(
        AgentSessionPersistenceCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        Task<AgentSessionPersistenceResult> wait;
        lock (_gate)
        {
            if (_results.TryGetValue(checkpoint.SessionId, out var results))
            {
                while (results.Count > 0 && results.Peek().CycleId <= checkpoint.CycleId)
                    results.Dequeue();

                if (results.Count == 0)
                    _results.Remove(checkpoint.SessionId);
                else
                {
                    var result = results.Dequeue();
                    if (results.Count == 0)
                        _results.Remove(checkpoint.SessionId);
                    return Task.FromResult(result);
                }
            }

            var waiter = new TaskCompletionSource<AgentSessionPersistenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(checkpoint.SessionId, out var waiters))
                _waiters[checkpoint.SessionId] = waiters = new Queue<Waiter>();
            waiters.Enqueue(new Waiter(checkpoint.CycleId, waiter));
            wait = WaitWithCancellationAsync(waiter, cancellationToken);
        }

        _advance?.Invoke();
        return wait;
    }

    private static async Task<AgentSessionPersistenceResult> WaitWithCancellationAsync(
        TaskCompletionSource<AgentSessionPersistenceResult> completion,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private long CurrentCycle(string sessionId) =>
        _cycles.TryGetValue(sessionId, out var cycleId) ? cycleId : 0;

    private sealed record Waiter(
        long MinimumCycleId,
        TaskCompletionSource<AgentSessionPersistenceResult> Completion);
}

public readonly record struct AgentSessionPersistenceCheckpoint(
    AgentSessionPersistenceTestProbe Probe,
    string SessionId,
    long CycleId)
{
    public Task<AgentSessionPersistenceResult> WaitAsync(CancellationToken cancellationToken = default) =>
        Probe.WaitForNextAsync(this, cancellationToken);
}

public static class AgentSessionPersistenceTestExtensions
{
    public static AgentSessionPersistenceCheckpoint PersistenceCheckpoint(
        this IAgentSessionGrain grain,
        AgentSessionPersistenceTestProbe probe) =>
        probe.Checkpoint(grain.GetPrimaryKeyString());
}
