using System.Collections.Concurrent;
using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Default in-process <see cref="IEventBus"/> implementation.
///
/// <para>
/// Subscriptions are <b>additive and permanent</b> — there is no
/// <c>Unsubscribe</c> API on the typed subscribe path, by design.
/// See the discussion on <see cref="IEventBus"/> for the rationale.
/// </para>
///
/// <para>
/// <b>Concurrency</b>. Each per-<c>type</c> handler list is guarded
/// by a single per-list lock. Both the dispatch snapshot and the
/// subscribe-append go through that lock, so the dispatch loop
/// always sees a stable view even if a <see cref="Subscribe(string, Func{CloudEvent, Task})"/>
/// call lands mid-dispatch. The legacy <c>On</c>/<c>Off</c>
/// pair is a separate per-list lock with the same discipline.
/// </para>
///
/// <para>
/// <b>Why this avoids the prior race</b>. The race in
/// <c>WorktreeCleanupService.StopAsync</c> was a
/// <c>Collection was modified; enumeration operation may not execute</c>
/// thrown by the .NET <c>List&lt;T&gt;.Enumerator</c>. That
/// enumerator belongs to a private <c>_subscriptions</c> list the
/// subscriber was iterating; the modification came from the host
/// running several <c>StopAsync</c> paths concurrently. With
/// <i>static</i> subscriptions — registered in
/// <c>StartAsync</c>, never removed at runtime — the
/// per-subscriber <c>StopAsync</c> body has no iteration to
/// invalidate, so the race class is closed off at the source
/// rather than defended against in every subscriber.
/// </para>
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, List<Action<object>>> _handlers = new();
    private readonly ConcurrentDictionary<string, List<SubscriptionEntry>> _typedHandlers = new();
    private readonly ILogger<InMemoryEventBus> _log;

    public InMemoryEventBus(ILogger<InMemoryEventBus> log)
    {
        _log = log;
    }

    // ── legacy string-name API (back-compat) ───────────────────────

    public void On(string eventName, Action<object> handler)
    {
        var list = _handlers.GetOrAdd(eventName, _ => new List<Action<object>>());
        lock (list) list.Add(handler);
    }

    public void Off(string eventName, Action<object> handler)
    {
        if (_handlers.TryGetValue(eventName, out var list))
        {
            lock (list) list.Remove(handler);
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

    // ── static, permanent subscribe ─────────────────────────────────

    public void Subscribe(string eventType, Func<CloudEvent, Task> handler)
        => SubscribeCore(bucketKey: eventType, filter: CloudEventFilter.Type(eventType), handler);

    public void Subscribe(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
        => SubscribeCore(
            bucketKey: filter.Method.GetHashCode().ToString(),
            filter,
            handler);

    private void SubscribeCore(string bucketKey, Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
    {
        var entry = new SubscriptionEntry(filter, handler);
        var list = _typedHandlers.GetOrAdd(bucketKey, _ => new List<SubscriptionEntry>());
        lock (list)
        {
            list.Add(entry);
        }
    }

    // ── dispatch paths ─────────────────────────────────────────────

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
        List<SubscriptionEntry> list,
        CloudEvent evt)
    {
        SubscriptionEntry[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        foreach (var entry in snapshot)
        {
            if (!entry.Filter(evt)) continue;
            // Schedule the handler on the thread pool. The bus
            // intentionally does not await this — see the
            // IEventBus doc on Emit. Exceptions are caught and
            // logged inside the wrapper, so the dispatch loop
            // never sees them.
            _ = RunHandlerSafely(entry, evt);
        }
    }

    private async Task DispatchTypedAsync(
        List<SubscriptionEntry> list,
        CloudEvent evt,
        CancellationToken ct)
    {
        SubscriptionEntry[] snapshot;
        lock (list)
        {
            snapshot = list.ToArray();
        }
        var tasks = new List<Task>(snapshot.Length);
        foreach (var entry in snapshot)
        {
            if (!entry.Filter(evt)) continue;
            tasks.Add(RunHandlerSafely(entry, evt));
        }
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task RunHandlerSafely(SubscriptionEntry entry, CloudEvent evt)
    {
        try
        {
            await entry.Handler(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Typed event handler failed for {Type}", evt.Type);
        }
    }

    private sealed class SubscriptionEntry
    {
        public readonly Func<CloudEvent, bool> Filter;
        public readonly Func<CloudEvent, Task> Handler;

        public SubscriptionEntry(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
        {
            Filter = filter;
            Handler = handler;
        }
    }
}

internal static class CloudEventFilter
{
    public static Func<CloudEvent, bool> Type(string type) => e => e.Type == type;
}
