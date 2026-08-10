using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerConnectionTracker : ISingletonService, IAgentSessionConnectionRegistry
{
    private readonly ConcurrentDictionary<string, RunnerConnectionLease> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();
    private readonly string _processEpoch = Guid.NewGuid().ToString("N");
    private long _nextConnectionGeneration;

    public string Register(string runnerId, string connectionId)
    {
        var generation = $"{_processEpoch}:{Interlocked.Increment(ref _nextConnectionGeneration)}";
        _connections[runnerId] = new RunnerConnectionLease(connectionId, generation);
        return generation;
    }

    public void Unregister(string runnerId, string? connectionId = null)
    {
        if (connectionId is null)
        {
            _connections.TryRemove(runnerId, out _);
            return;
        }

        if (_connections.TryGetValue(runnerId, out var current)
            && string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            _connections.TryRemove(new KeyValuePair<string, RunnerConnectionLease>(runnerId, current));
        }
    }

    public IReadOnlyList<string> UnregisterAndGetSessions(string runnerId, string connectionId)
    {
        if (!_connections.TryGetValue(runnerId, out var current)
            || !string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal)
            || !_connections.TryRemove(new KeyValuePair<string, RunnerConnectionLease>(runnerId, current)))
            return [];

        if (!_sessions.TryRemove(runnerId, out var sessions)) return [];
        return sessions.Keys.ToArray();
    }

    public void RegisterSession(string runnerId, string sessionId) =>
        _sessions.GetOrAdd(runnerId, _ => new ConcurrentDictionary<string, byte>())[sessionId] = 0;

    public string? GetConnectionId(string runnerId)
    {
        return _connections.TryGetValue(runnerId, out var connection)
            ? connection.ConnectionId
            : null;
    }

    public string? GetConnectionGeneration(string runnerId)
    {
        return _connections.TryGetValue(runnerId, out var connection)
            ? connection.Generation
            : null;
    }

    private sealed record RunnerConnectionLease(string ConnectionId, string Generation);
}
