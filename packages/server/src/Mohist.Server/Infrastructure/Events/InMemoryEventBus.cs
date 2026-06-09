using System.Collections.Concurrent;
using System.Reflection;
using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<string, List<SubscriptionEntry>> _typedHandlers = new();
    private readonly ConcurrentDictionary<string, Type> _handlerInterfaceByType = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InMemoryEventBus> _log;

    public InMemoryEventBus(
        IServiceScopeFactory scopes,
        ILogger<InMemoryEventBus> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public InMemoryEventBus(ILogger<InMemoryEventBus> log)
        : this(NullScopeFactory.Instance, log)
    {
    }

    private sealed class NullScopeFactory : IServiceScopeFactory
    {
        public static readonly NullScopeFactory Instance = new();
        public IServiceScope CreateScope() => new NullScope();
    }

    private sealed class NullScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new NullServiceProvider();
        public void Dispose() { }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    public void Emit(CloudEvent cloudEvent)
    {
        EmitAsync(cloudEvent, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task EmitAsync(CloudEvent cloudEvent, CancellationToken ct = default)
    {
        var type = cloudEvent.Type ?? string.Empty;

        await DispatchTypedHandlersAsync(cloudEvent, ct).ConfigureAwait(false);

        if (_typedHandlers.TryGetValue(type, out var typedList))
        {
            await DispatchTypedAsync(typedList, cloudEvent, ct).ConfigureAwait(false);
        }
    }

    public void Subscribe(string eventType, Func<CloudEvent, Task> handler)
        => SubscribeCore(bucketKey: eventType, filter: CloudEventFilter.Type(eventType), handler);

    public void Subscribe(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
        => SubscribeCore(
            bucketKey: filter.Method.GetHashCode().ToString(),
            filter,
            handler);

    public void RegisterHandlerInterfaces(
        IReadOnlyDictionary<string, Type> eventTypeToHandlerInterface)
    {
        foreach (var (eventType, handlerInterface) in eventTypeToHandlerInterface)
        {
            if (handlerInterface is null)
            {
                throw new ArgumentException(
                    $"Handler interface for event type '{eventType}' is null", nameof(eventTypeToHandlerInterface));
            }
            if (!handlerInterface.IsInterface)
            {
                throw new ArgumentException(
                    $"Handler interface for event type '{eventType}' must be an interface (got {handlerInterface.Name})",
                    nameof(eventTypeToHandlerInterface));
            }
            _handlerInterfaceByType[eventType] = handlerInterface;
        }
        _log.LogInformation(
            "InMemoryEventBus registered {Count} typed handler interface(s): {Types}",
            _handlerInterfaceByType.Count,
            string.Join(", ", _handlerInterfaceByType.Keys));
    }

    private void SubscribeCore(string bucketKey, Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler)
    {
        var entry = new SubscriptionEntry(filter, handler);
        var list = _typedHandlers.GetOrAdd(bucketKey, _ => new List<SubscriptionEntry>());
        lock (list)
        {
            list.Add(entry);
        }
    }

    private async Task DispatchTypedHandlersAsync(CloudEvent evt, CancellationToken ct)
    {
        var type = evt.Type ?? string.Empty;
        if (!_handlerInterfaceByType.TryGetValue(type, out var handlerInterface)) return;

        using var scope = _scopes.CreateScope();
        var handlers = scope.ServiceProvider.GetServices(handlerInterface);
        var handlerList = handlers.ToList();
        if (handlerList.Count == 0) return;

        var tasks = new List<Task>(handlerList.Count);
        foreach (var handler in handlerList)
        {
            if (handler is null) continue;
            tasks.Add(InvokeHandlerSafely(handler, handlerInterface, evt));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task InvokeHandlerSafely(object handler, Type handlerInterface, CloudEvent evt)
    {
        try
        {
            var method = handlerInterface.GetMethod(
                "HandleAsync",
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"Handler interface {handlerInterface.Name} has no public HandleAsync method");

            var result = method.Invoke(handler, [evt, CancellationToken.None]);
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
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
