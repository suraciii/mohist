using System.Collections.Concurrent;

namespace Mohist.Server.Sessions.Services;

public sealed class AgentSessionStopClaimRegistry : IAgentSessionStopClaimRegistry
{
    private readonly ConcurrentDictionary<(string SessionId, string TurnId), ConcurrentDictionary<string, byte>> _active = new();

    public IDisposable Register(string sessionId, string turnId)
    {
        var key = (sessionId, turnId);
        var operationId = Guid.NewGuid().ToString("N");
        var registrations = _active.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>());
        registrations[operationId] = 0;
        return new Registration(this, key, operationId);
    }

    public bool IsActive(string sessionId, string turnId) =>
        _active.TryGetValue((sessionId, turnId), out var registrations) && !registrations.IsEmpty;

    private void Unregister((string SessionId, string TurnId) key, string operationId)
    {
        if (!_active.TryGetValue(key, out var registrations))
            return;

        _ = registrations.TryRemove(operationId, out _);
        if (registrations.IsEmpty)
            _active.TryRemove(new KeyValuePair<(string SessionId, string TurnId), ConcurrentDictionary<string, byte>>(key, registrations));
    }

    private sealed class Registration(
        AgentSessionStopClaimRegistry owner,
        (string SessionId, string TurnId) key,
        string operationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unregister(key, operationId);
        }
    }
}
