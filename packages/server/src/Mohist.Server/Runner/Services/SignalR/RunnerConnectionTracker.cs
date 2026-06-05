using System.Collections.Concurrent;

namespace Mohist.Server.Runner.Services.SignalR;

public class RunnerConnectionTracker
{
    private readonly ConcurrentDictionary<string, string> _connections = new();

    public void Register(string runnerId, string connectionId)
    {
        _connections[runnerId] = connectionId;
    }

    public void Unregister(string runnerId)
    {
        _connections.TryRemove(runnerId, out _);
    }

    public string? GetConnectionId(string runnerId)
    {
        _connections.TryGetValue(runnerId, out var connectionId);
        return connectionId;
    }
}
