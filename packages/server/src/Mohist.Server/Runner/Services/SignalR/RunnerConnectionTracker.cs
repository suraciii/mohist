using System.Collections.Concurrent;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerConnectionTracker : ISingletonService, IAgentSessionConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();

    public void Register(string runnerId, string connectionId)
    {
        _connections[runnerId] = connectionId;
    }

    public void Unregister(string runnerId)
    {
        _connections.TryRemove(runnerId, out _);
    }

    public IReadOnlyList<string> UnregisterAndGetSessions(string runnerId)
    {
        _connections.TryRemove(runnerId, out _);
        if (!_sessions.TryRemove(runnerId, out var sessions)) return [];
        return sessions.Keys.ToArray();
    }

    public void RegisterSession(string runnerId, string sessionId) =>
        _sessions.GetOrAdd(runnerId, _ => new ConcurrentDictionary<string, byte>())[sessionId] = 0;

    public string? GetConnectionId(string runnerId)
    {
        _connections.TryGetValue(runnerId, out var connectionId);
        return connectionId;
    }
}
