using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.TestSupport;

public sealed class AgentSessionStatePersistenceFailureProbe
{
    private int _failuresRemaining;

    public void QueueFailures(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _failuresRemaining, count);
    }

    public void Reset() => Interlocked.Exchange(ref _failuresRemaining, 0);

    internal void ThrowIfQueued()
    {
        if (Interlocked.CompareExchange(ref _failuresRemaining, 0, 0) <= 0)
            return;

        Interlocked.Decrement(ref _failuresRemaining);
        throw new InvalidOperationException("simulated AgentSession state persistence failure");
    }
}

public sealed class FailingAgentSessionStore : IAgentSessionStore
{
    private readonly IAgentSessionStore _inner;
    private readonly AgentSessionStatePersistenceFailureProbe _failures;

    public FailingAgentSessionStore(
        IAgentSessionStore inner,
        AgentSessionStatePersistenceFailureProbe failures)
    {
        _inner = inner;
        _failures = failures;
    }

    public Task<AgentSession?> LoadAsync(string key) => _inner.LoadAsync(key);

    public Task<IReadOnlyList<AgentSession>> ListAsync() => _inner.ListAsync();

    public Task<IReadOnlyList<AgentSessionReconcileBinding>> ListByRunnerForReconcileAsync(
        string runnerId,
        CancellationToken ct = default) => _inner.ListByRunnerForReconcileAsync(runnerId, ct);

    public Task SaveAsync(string key, AgentSession state)
    {
        _failures.ThrowIfQueued();
        return _inner.SaveAsync(key, state);
    }

    public Task SaveAsync(
        string key,
        AgentSession state,
        IReadOnlyList<AgentSessionEvent> events,
        CancellationToken ct = default)
    {
        _failures.ThrowIfQueued();
        return _inner.SaveAsync(key, state, events, ct);
    }

    public Task DeleteAsync(string key) => _inner.DeleteAsync(key);
}
