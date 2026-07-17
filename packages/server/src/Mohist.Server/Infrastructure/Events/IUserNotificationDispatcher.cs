using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Decides whether a CloudEvent should be delivered to a connected
/// SignalR user, and to which SignalR groups. The bus never knows
/// about users; the SignalR hub never knows about handlers.
/// <see cref="IUserNotificationDispatcher"/> is the only place where
/// "which event should which user see" is computed.
///
/// <para>
/// The split exists because the two concerns have different
/// lifetimes:
/// <list type="bullet">
///   <item><b>Bus subscriptions</b> are static — registered at
///         process start, torn down at process stop. They are
///         part of the program's source. There are typically a
///         dozen of them.</item>
///   <item><b>User subscriptions</b> are dynamic — added when a
///         user opens the issues board, removed when the user
///         closes the tab. They are part of the user's state.
///         There can be thousands of them.</item>
/// </list>
/// Mixing the two on a single bus means the bus has to know about
/// "user" — a concept that has no business being in an in-process
/// pub/sub. The dispatcher hides that.
/// </para>
///
/// <para>
/// <b>Shape</b>. A dispatcher implementation typically:
/// <list type="number">
///   <item>Receives an event from <see cref="IEventBridge"/>.</item>
///   <item>Asks a <c>UserSubscriptionGrain</c> (or any durable store)
///         which users are interested in this event's
///         <c>projectid</c> / <c>issue</c> / event-type.</item>
///   <item>Filters the event against each user's
///         <c>subscribedEventTypes</c> set.</item>
///   <item>Returns the set of SignalR connection IDs that should
///         receive the event.</item>
/// </list>
/// Step 2 must be cheap. The <c>UserSubscriptionGrain</c> holds the
/// per-user subscription set in memory; the dispatcher fans out by
/// reading one grain call per event. The SignalR <c>HubContext</c>
/// is asked to push to a set of connection IDs once at the end.
/// </para>
/// </summary>
public interface IUserNotificationDispatcher
{
    /// <summary>
    /// Resolve the SignalR connection IDs that should receive this
    /// event. Returns the connection IDs as a set so the caller can
    /// deduplicate without further bookkeeping. The returned set may
    /// be empty — that is a normal "no user is currently interested"
    /// result, not an error.
    /// </summary>
    /// <remarks>
    /// Implementations must be safe to call from a hosted service's
    /// handler thread. They may use <c>await</c> for the grain
    /// call but must not block indefinitely; if the grain is
    /// unavailable, the dispatcher should log and return an empty
    /// set rather than hold up the dispatch loop.
    /// </remarks>
    Task<IReadOnlySet<string>> ResolveTargetConnectionsAsync(
        CloudEvent cloudEvent,
        CancellationToken ct);
}
