using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.SpecTests.Support;

public sealed class AgentSessionPersistenceTestProbe : IAgentSessionPersistenceObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<AgentSessionPersistenceResult>> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TaskCompletionSource<AgentSessionPersistenceResult>>> _waiters = new(StringComparer.Ordinal);
    private readonly Action? _advance;

    public AgentSessionPersistenceTestProbe(Action? advance = null)
    {
        _advance = advance;
    }

    public void Report(AgentSessionPersistenceResult result)
    {
        TaskCompletionSource<AgentSessionPersistenceResult>? waiter = null;
        lock (_gate)
        {
            if (_waiters.TryGetValue(result.SessionId, out var waiters) && waiters.Count > 0)
            {
                waiter = waiters.Dequeue();
                if (waiters.Count == 0)
                    _waiters.Remove(result.SessionId);
            }
            else
            {
                if (!_results.TryGetValue(result.SessionId, out var results))
                    _results[result.SessionId] = results = new Queue<AgentSessionPersistenceResult>();
                results.Enqueue(result);
            }
        }

        waiter?.SetResult(result);
    }

    public Task<AgentSessionPersistenceResult> WaitForNextAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Task<AgentSessionPersistenceResult> wait;
        lock (_gate)
        {
            if (_results.TryGetValue(sessionId, out var results) && results.Count > 0)
            {
                var result = results.Dequeue();
                if (results.Count == 0)
                    _results.Remove(sessionId);
                return Task.FromResult(result);
            }

            var waiter = new TaskCompletionSource<AgentSessionPersistenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(sessionId, out var waiters))
                _waiters[sessionId] = waiters = new Queue<TaskCompletionSource<AgentSessionPersistenceResult>>();
            waiters.Enqueue(waiter);
            wait = waiter.Task.WaitAsync(cancellationToken);
        }

        _advance?.Invoke();
        return wait;
    }

}

public static class AgentSessionPersistenceTestExtensions
{
    public static Task<AgentSessionPersistenceResult> WaitForPersistenceAsync(
        this IAgentSessionGrain grain,
        AgentSessionPersistenceTestProbe probe,
        CancellationToken cancellationToken = default)
        => probe.WaitForNextAsync(
            grain.GetPrimaryKeyString(),
            cancellationToken);
}
