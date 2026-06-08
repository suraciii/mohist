using System.Collections.Concurrent;

namespace Mohist.Server.User.Grains;

/// <summary>
/// Per-SignalR-connection subscription state, owned by an Orleans
/// grain keyed by <c>connectionId</c>. This is the
/// <i>long-form</i> replacement for the prior process-local
/// <c>RunnerConnectionTracker</c>-style registry that the
/// <c>MohistHub</c> maintained. The grain survives silo restarts,
/// so a SignalR client that reconnects after the server bounced
/// reattaches to its existing subscription state. Cross-replica
/// routing is also handled for free: any silo can ask any
/// connection's grain "do you want this event?" without needing a
/// shared in-process map.
///
/// <para>
/// <b>Relationship to <see cref="ConnectionSubscriptionRegistry"/></b>.
/// The registry is a process-local hot-path mirror of the grain
/// state, kept up-to-date by the <c>MohistHub</c>'s
/// <c>Subscribe</c>/<c>Unsubscribe</c> hub methods. The
/// <see cref="UserNotificationDispatcher"/> reads the registry on
/// every emit; the grain is consulted only on connection
/// open/reconnect. This split mirrors the Orleans + SignalR
/// reference pattern from
/// <c>viking://agent/default/memories/trajectories/SignalR远程Runner通信架构_20260530045922.md</c>:
/// grain owns durable state, the transport (SignalR) owns
/// transient connection identity.
/// </para>
///
/// <para>
/// <b>Why per-connection, not per-user</b>. A single user can
/// have two tabs open at once — the issues board and the workflow
/// runner page. They want different events: the issues board subscribes
/// to issue-level events; the workflow runner page subscribes to
/// workflow-run events. Per-user subscription state would force
/// the two tabs to compete; per-connection state keeps each tab's
/// view independent and is automatically released when the tab
/// closes (Orleans deactivates the grain after the connection's
/// SignalR deactivation, the state's deactivation handler clears
/// the in-memory set, and there is no other consumer to confuse).
/// </para>
/// </summary>
public interface IConnectionSubscriptionGrain : IGrainWithStringKey
{
    /// <summary>
    /// Replace the connection's subscription set with
    /// <paramref name="eventTypes"/>. Idempotent. Used by the
    /// SignalR hub to apply the client's "subscribe to these
    /// event types" request as one atomic operation.
    /// </summary>
    Task SetSubscriptionsAsync(IReadOnlyCollection<string> eventTypes);

    /// <summary>Add one event type. No-op if already present.</summary>
    Task SubscribeAsync(string eventType);

    /// <summary>Remove one event type. No-op if not present.</summary>
    Task UnsubscribeAsync(string eventType);

    /// <summary>
    /// Read the current subscription set as a snapshot copy.
    /// Used by the SignalR hub to (re)populate the
    /// <see cref="ConnectionSubscriptionRegistry"/> on connection
    /// open / reconnect.
    /// </summary>
    Task<IReadOnlySet<string>> GetSubscriptionsAsync();
}
