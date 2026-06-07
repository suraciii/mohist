using System.Collections.Concurrent;
using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<object>>> _handlers = new();
    private readonly ConcurrentDictionary<string, List<(Func<CloudEvent, bool> Filter, Action<CloudEvent> Handler)>> _typedHandlers = new();
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
            DispatchLegacy(list, eventName, data);
        }

        if (_typedHandlers.TryGetValue(eventName, out var typedList))
        {
            var envelope = data as CloudEvent
                ?? CloudEventFactory.Create(
                    type: eventName,
                    source: new Uri("about:blank", UriKind.Absolute),
                    data: data);
            DispatchTyped(typedList, envelope);
        }
    }

    public void Emit(CloudEvent cloudEvent)
    {
        var type = cloudEvent.Type ?? string.Empty;

        if (_handlers.TryGetValue(type, out var legacyList))
        {
            DispatchLegacy(legacyList, type, cloudEvent);
        }

        if (_typedHandlers.TryGetValue(type, out var typedList))
        {
            DispatchTyped(typedList, cloudEvent);
        }
    }

    public IDisposable OnType(string type, Action<CloudEvent> handler)
    {
        var entry = (Filter: CloudEventFilter.Type(type), Handler: handler);
        var list = _typedHandlers.GetOrAdd(type, _ => new List<(Func<CloudEvent, bool>, Action<CloudEvent>)>());
        lock (list)
        {
            list.Add(entry);
        }
        return new Subscription(() => OffType(type, entry));
    }

    public IDisposable OnAny(Func<CloudEvent, bool> filter, Action<CloudEvent> handler)
    {
        var entry = (Filter: filter, Handler: handler);
        var key = filter.Method.GetHashCode().ToString();
        var list = _typedHandlers.GetOrAdd(key, _ => new List<(Func<CloudEvent, bool>, Action<CloudEvent>)>());
        lock (list)
        {
            list.Add(entry);
        }
        return new Subscription(() =>
        {
            if (_typedHandlers.TryGetValue(key, out var l))
            {
                lock (l) l.Remove(entry);
            }
        });
    }

    private void OffType(string type, (Func<CloudEvent, bool> Filter, Action<CloudEvent> Handler) entry)
    {
        if (_typedHandlers.TryGetValue(type, out var list))
        {
            lock (list)
            {
                list.Remove(entry);
            }
        }
    }

    private void DispatchLegacy(List<Action<object>> list, string eventName, object data)
    {
        Action<object>[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var handler in snapshot)
        {
            try
            {
                handler(data);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Event handler failed for {Event}", eventName);
            }
        }
    }

    private void DispatchTyped(List<(Func<CloudEvent, bool> Filter, Action<CloudEvent> Handler)> list, CloudEvent evt)
    {
        (Func<CloudEvent, bool> Filter, Action<CloudEvent> Handler)[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var (filter, handler) in snapshot)
        {
            if (!filter(evt)) continue;
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Typed event handler failed for {Event}", evt.Type);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;
        public Subscription(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _onDispose();
        }
    }
}

internal static class CloudEventFilter
{
    public static Func<CloudEvent, bool> Type(string type) => e => e.Type == type;
}
