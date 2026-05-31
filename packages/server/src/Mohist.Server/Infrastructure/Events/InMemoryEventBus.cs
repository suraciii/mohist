using System.Collections.Concurrent;

namespace Mohist.Server.Infrastructure.Events;

public class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<object>>> _handlers = new();
    private readonly ILogger<InMemoryEventBus> _log;

    public InMemoryEventBus(ILogger<InMemoryEventBus> log)
    {
        _log = log;
    }

    public void On(string eventName, Action<object> handler)
    {
        var list = _handlers.GetOrAdd(eventName, _ => new List<Action<object>>());
        lock (list)
        {
            list.Add(handler);
        }
    }

    public void Off(string eventName, Action<object> handler)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            lock (list)
            {
                list.Remove(handler);
            }
        }
    }

    public void Emit(string eventName, object data)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            Action<object>[] snapshot;
            lock (list)
            {
                snapshot = list.ToArray();
            }
            foreach (var handler in snapshot)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(data);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Event handler failed for {Event}", eventName);
                    }
                });
            }
        }
    }
}
