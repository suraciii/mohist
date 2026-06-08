using System.Collections.Concurrent;
using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<object>>> _handlers = new();
    private readonly ConcurrentDictionary<string, List<(Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler)>> _typedHandlers = new();
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
            DispatchTypedFireAndForget(typedList, envelope);
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
            DispatchTypedFireAndForget(typedList, cloudEvent);
        }
    }

    public async Task EmitAsync(CloudEvent cloudEvent, CancellationToken ct = default)
    {
        var type = cloudEvent.Type ?? string.Empty;

        if (_handlers.TryGetValue(type, out var legacyList))
        {
            DispatchLegacy(legacyList, type, cloudEvent);
        }

        if (_typedHandlers.TryGetValue(type, out var typedList))
        {
            await DispatchTypedAsync(typedList, cloudEvent, ct).ConfigureAwait(false);
        }
    }

    public IDisposable OnType(string type, Func<CloudEvent, Task> handler)
    {
        var entry = (Filter: CloudEventFilter.Type(type), Handler: handler);
        var list = _typedHandlers.GetOrAdd(type, _ => new List<(Func<CloudEvent, bool>, Func<CloudEvent, Task>)>());
        lock (list)
        {
            list.Add(entry);
        }
        return new Subscription(() => OffType(type, entry));
    }

    public IDisposable OnAny(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
    {
        var entry = (Filter: filter, Handler: handler);
        var key = filter.Method.GetHashCode().ToString();
        var list = _typedHandlers.GetOrAdd(key, _ => new List<(Func<CloudEvent, bool>, Func<CloudEvent, Task>)>());
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

    private void OffType(string type, (Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler) entry)
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

    private void DispatchTypedFireAndForget(
        List<(Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler)> list,
        CloudEvent evt)
    {
        (Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler)[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var (filter, handler) in snapshot)
        {
            if (!filter(evt)) continue;
            _ = RunHandlerAsync(handler, evt);
        }
    }

    private async Task DispatchTypedAsync(
        List<(Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler)> list,
        CloudEvent evt,
        CancellationToken ct)
    {
        (Func<CloudEvent, bool> Filter, Func<CloudEvent, Task> Handler)[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        var tasks = new List<Task>(snapshot.Length);
        foreach (var (filter, handler) in snapshot)
        {
            if (!filter(evt)) continue;
            tasks.Add(RunHandlerAsync(handler, evt));
        }
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunHandlerAsync(Func<CloudEvent, Task> handler, CloudEvent evt)
    {
        try
        {
            await handler(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Typed event handler failed for {Event}", evt.Type);
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
