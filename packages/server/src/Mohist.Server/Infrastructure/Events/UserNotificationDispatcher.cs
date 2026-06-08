using System.Collections.Concurrent;
using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Process-local hot-path mirror of the per-connection subscription
/// set held in <see cref="Mohist.Server.User.Grains.IConnectionSubscriptionGrain"/>.
/// Maintained by <c>MohistHub</c> on every connect / disconnect /
/// <c>Subscribe</c> / <c>Unsubscribe</c>; read by
/// <see cref="UserNotificationDispatcher"/> on every bus emit.
///
/// <para>
/// <b>Why mirror</b>. Asking the grain for
/// <c>ShouldNotify</c> on every emit × every connection is O(N)
/// grain calls per emit. For a single-silo deployment with a
/// handful of open tabs this is fine; the mirror becomes essential
/// the moment we have more than a low-double-digit number of
/// connections or more than one silo. The mirror is
/// <i>authoritative for the dispatcher</i>; the grain is
/// authoritative for "what is the durable state of record" and
/// is the source the mirror is rebuilt from on connection
/// open / reconnect.
/// </para>
///
/// <para>
/// <b>Why not just SignalR groups</b>. A SignalR group membership
/// is process-local to the silo that hosts the hub. With one
/// silo this is equivalent to a registry; with multiple silos,
/// the membership is only known to the silo that handled the
/// <c>Groups.AddToGroupAsync</c> call. Routing the dispatcher
/// through groups therefore needs an extra "which silo has the
/// connection" round trip, which is what the grain + connectionId
/// + IHubContext pattern already gives us for free. The registry
/// is a thin cache, not a replacement for that pattern.
/// </para>
/// </summary>
public sealed class ConnectionSubscriptionRegistry
{
    /// <summary>
    /// connectionId → set of event types the connection wants. A
    /// connection is registered on SignalR <c>OnConnectedAsync</c>
    /// and unregistered on <c>OnDisconnectedAsync</c>. An empty
    /// set means "the client has not yet called Subscribe" — the
    /// dispatcher will not push anything to such a connection, which
    /// is the correct default for a freshly opened tab.
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new(StringComparer.Ordinal);

    /// <summary>
    /// Snapshot of all currently-tracked connection IDs. Read by
    /// <see cref="UserNotificationDispatcher"/> on every emit.
    /// </summary>
    public IReadOnlyCollection<string> ConnectionIds
    {
        get
        {
            // Materialise the ConcurrentDictionary's KeyCollection
            // into a snapshot list. The KeyCollection is
            // ICollection<T> and snapshots well.
            lock (_byConnection)
            {
                return _byConnection.Keys.ToList();
            }
        }
    }

    public void RegisterConnection(string connectionId)
    {
        _byConnection.TryAdd(connectionId, new HashSet<string>(StringComparer.Ordinal));
    }

    public void UnregisterConnection(string connectionId)
    {
        _byConnection.TryRemove(connectionId, out _);
    }

    public void SetSubscriptions(string connectionId, IReadOnlyCollection<string> eventTypes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (eventTypes is not null)
        {
            foreach (var t in eventTypes)
            {
                if (!string.IsNullOrEmpty(t)) set.Add(t);
            }
        }
        _byConnection[connectionId] = set;
    }

    public void Subscribe(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return;
        var set = _byConnection.GetOrAdd(connectionId, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set) { set.Add(eventType); }
    }

    public void Unsubscribe(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return;
        if (_byConnection.TryGetValue(connectionId, out var set))
        {
            lock (set) { set.Remove(eventType); }
        }
    }

    public bool ShouldNotify(string connectionId, string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return false;
        return _byConnection.TryGetValue(connectionId, out var set)
            && set.Contains(eventType);
    }
}

/// <summary>
/// Default <see cref="IUserNotificationDispatcher"/> implementation:
/// for one incoming <see cref="CloudEvent"/>, ask the
/// <see cref="ConnectionSubscriptionRegistry"/> "do you want
/// this?" for every active connection and return the set of
/// connection IDs that said yes. The
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/> then pushes
/// the event to those connection IDs via SignalR
/// <c>IHubContext.Clients.Client(connectionId)</c>.
///
/// <para>
/// <b>Cost model</b>. One dispatcher call is O(N) over the
/// current connection set, where N is the number of open browser
/// tabs. The check per connection is a single hash-set lookup, no
/// allocation, no I/O. N is bounded by the deployment size; at the
/// scales Mohist actually runs (low double digits in test, low
/// hundreds in production) this is the right shape.
/// </para>
///
/// <para>
/// <b>Why not SignalR groups</b>. See the discussion on
/// <see cref="ConnectionSubscriptionRegistry"/> — groups are
/// process-local, the registry/grain pair is portable across
/// silos.
/// </para>
/// </summary>
public sealed class UserNotificationDispatcher : IUserNotificationDispatcher
{
    private readonly ConnectionSubscriptionRegistry _registry;

    public UserNotificationDispatcher(ConnectionSubscriptionRegistry registry)
    {
        _registry = registry;
    }

    public Task<IReadOnlySet<string>> ResolveTargetConnectionsAsync(
        CloudEvent cloudEvent,
        CancellationToken ct)
    {
        var eventType = cloudEvent.Type ?? string.Empty;
        if (string.IsNullOrEmpty(eventType))
        {
            return Task.FromResult<IReadOnlySet<string>>(_empty);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connectionId in _registry.ConnectionIds)
        {
            if (_registry.ShouldNotify(connectionId, eventType))
            {
                result.Add(connectionId);
            }
        }
        return Task.FromResult<IReadOnlySet<string>>(result);
    }

    private static readonly IReadOnlySet<string> _empty =
        new HashSet<string>(StringComparer.Ordinal);
}
