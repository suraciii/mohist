using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// In-process pub/sub for CloudEvents 1.0.2 envelopes. The Mohist
/// runtime carries two distinct concerns, deliberately on the same
/// wire shape but with two different APIs:
///
/// <list type="bullet">
///   <item><b>Static, internal subscriptions</b> — backend components
///         reacting to domain events. Registered at
///         <c>StartAsync</c>, lifetime = process lifetime, never
///         disposed. <i>Examples</i>:
///         <c>WorktreeCleanupService</c> reacting to
///         <c>workflow.run.completed</c>,
///         <c>AgentSessionRunnerBridge</c> reacting to
///         <c>runner.disconnected</c>, <c>IssueGrain</c> reacting to
///         <c>workflow.run.completed</c>. This interface is the
///         contract for them.</item>
///   <item><b>Dynamic, per-user subscriptions</b> — events that may
///         need to fan out to a connected UI client. The bus does
///         <i>not</i> know about users. The
///         <see cref="IUserNotificationDispatcher"/> is the only
///         place where "which event should which user see" is
///         computed. The
///         <see cref="Mohist.Server.Events.Hub.EventBridge"/>
///         bridges the bus into the dispatcher.</item>
/// </list>
///
/// <para>
/// <b>Why no Unsubscribe</b>. Internal subscriptions are
/// <i>part of the program's source</i>. If
/// <c>WorktreeCleanupService</c> were allowed to unsubscribe at
/// runtime, the cleanup behaviour would depend on call order
/// (<c>StartAsync</c> then unsubscribe then re-subscribe would be a
/// different sequence from <c>StartAsync</c> alone). That is a
/// well-known source of bugs in pub/sub systems: state that "should
/// be there" depends on something the developer did not model. The
/// static-only contract is the long-form fix: subscriptions are part
/// of the type's source, and the only way to remove one is to delete
/// the call to <see cref="Subscribe"/>.
/// </para>
///
/// <para>
/// <b>Why no Drain</b>. With no Unsubscribe there is no path that
/// needs to wait for in-flight handlers to complete. Process
/// shutdown kills in-flight handlers via the runtime's own
/// teardown; user-perceived latency on shutdown is bounded by
/// the <c>ShutdownTimeout</c> host option, not by a hand-rolled
/// drain. The previous design's per-subscription
/// <c>DisposeAllAsync</c> existed to plug a race in
/// <c>WorktreeCleanupService.StopAsync</c>; with static
/// subscriptions there is no StopAsync-vs-Unsubscribe interleaving
/// to race on.
/// </para>
///
/// <para>
/// <b>Concurrency</b>. The handler list per <c>type</c> is guarded
/// by a single per-list lock. Reads (dispatch snapshots) and writes
/// (subscribe) both go through that lock. This is unchanged from
/// the prior bus; the simplification above is the public
/// surface, not the locking discipline.
/// </para>
/// </summary>
public interface IEventBus
{
    /// <summary>Legacy string-name subscribe (back-compat).</summary>
    void On(string eventName, Action<object> handler);

    /// <summary>Unsubscribe a legacy <see cref="On"/> handler.</summary>
    /// <remarks>
    /// Kept on the public surface for back-compat with the existing
    /// legacy <c>On</c>/<c>Off</c> pairs. New code should use
    /// <see cref="Subscribe(string, Func{CloudEvent, Task})"/> which
    /// is static by contract — no Off needed.
    /// </remarks>
    void Off(string eventName, Action<object> handler);

    /// <summary>Legacy string-name emit. Constructs a minimal envelope.</summary>
    void Emit(string eventName, object data);

    /// <summary>
    /// Fire-and-forget CloudEvents 1.0.2 emit. Subscribers run
    /// concurrently; the bus logs per-handler exceptions; the
    /// caller's continuation runs immediately. Use from hot paths
    /// (grain methods emitting workflow lifecycle events).
    /// </summary>
    void Emit(CloudEvent cloudEvent);

    /// <summary>
    /// Awaiting emit. Returns a <see cref="Task"/> that completes
    /// when every matching typed subscriber has finished running. Use
    /// from hosted services that need to surface subscriber errors
    /// (so a subscriber's <c>InvalidOperationException</c> does not
    /// surface later as <c>UnobservedTaskException</c> on the next
    /// GC) or coordinate with graceful shutdown.
    /// </summary>
    Task EmitAsync(CloudEvent cloudEvent, CancellationToken ct = default);

    /// <summary>
    /// Static, process-lifetime subscription. <paramref name="handler"/>
    /// is invoked for every event whose <see cref="CloudEvent.Type"/>
    /// matches <paramref name="eventType"/>. The subscription
    /// cannot be undone at runtime — restart the process to
    /// remove it.
    /// </summary>
    void Subscribe(string eventType, Func<CloudEvent, Task> handler);

    /// <summary>
    /// Static, process-lifetime subscription via a predicate over
    /// the full envelope. Use for filters over extension
    /// attributes (<c>projectid</c>, <c>issueno</c>,
    /// <c>workflowrunid</c>) that cannot be expressed as a single
    /// <c>type</c> match. Cannot be undone at runtime.
    /// </summary>
    void Subscribe(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler);
}
