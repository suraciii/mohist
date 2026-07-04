using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.User.Grains;

namespace Mohist.Server.Events.Hub;

public interface IEventsClient
{
    /// <summary>
    /// Receive a domain event from the CloudEventBus. <paramref name="eventName"/> is the
    /// CloudEvents <c>type</c> for back-compat; <paramref name="data"/>
    /// carries a <see cref="CloudEventEnvelope"/> with the full CloudEvents
    /// 1.0.2 attributes (id, source, type, subject, time, extensions, data).
    /// New Web code should read from <c>envelope</c> in <paramref name="data"/>.
    /// </summary>
    Task OnEvent(string eventName, object? data);

    /// <summary>
    /// Receive a transcript (non-domain runtime) event from the
    /// dedicated <see cref="ITranscriptEventPublisher"/> channel. Carries a
    /// <see cref="TranscriptEnvelope"/> whose <c>Type</c> is the generic
    /// session event name (e.g. <c>message.delta</c> or
    /// <c>tool_call.started</c>) and whose <c>Payload</c> is the deserialised
    /// payload JSON the runner sent. The <c>OnEvent</c> channel is intentionally
    /// separate: domain events and transcript events flow on physically
    /// distinct SignalR methods so the Web can subscribe to one without
    /// the other.
    /// </summary>
    Task OnTranscriptEvent(TranscriptEnvelope envelope);

    /// <summary>
    /// Receive a task-log (non-domain runtime) delta from the
    /// dedicated <see cref="ITaskLogDeltaPublisher"/> channel. Carries
    /// a <see cref="TaskLogDeltaEnvelope"/> whose
    /// <c>(ownerKind, ownerId, workId, taskId)</c> identify the
    /// work item whose execution log has just been persisted, and
    /// whose <c>entries</c> are the newly persisted lines
    /// (deduplicated by <c>Seq</c> on the server before publish).
    ///
    /// <para>
    /// Physically separate from both <see cref="OnEvent"/> (domain
    /// events, CloudEventBus) and <see cref="OnTranscriptEvent"/>
    /// (session-scoped runtime). A task-log delta never appears on
    /// the transcript channel and a transcript envelope never
    /// appears on this channel — the three channels are independent
    /// SignalR methods each with its own publisher and its own
    /// subscription filter.
    /// </para>
    /// </summary>
    Task OnTaskLogDelta(TaskLogDeltaEnvelope envelope);
}

/// <summary>
/// SignalR hub for the Web UI. Each connection owns a
/// <see cref="Mohist.Server.User.Grains.IConnectionSubscriptionGrain"/>
/// keyed by <c>connectionId</c>; the Web UI calls
/// <see cref="SubscribeAsync"/> / <see cref="UnsubscribeAsync"/> to
/// shape which events it gets. The bus-driven
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/> reads
/// <see cref="Mohist.Server.Infrastructure.Events.ConnectionSubscriptionRegistry"/>
/// to decide which connections receive each event and pushes via
/// <see cref="IHubClients{T}.Client(string)"/>.
///
/// <para>
/// <b>Three channels, one hub</b>. The hub exposes
/// <see cref="IEventsClient.OnEvent"/> (domain events, fanned out
/// by <see cref="EventBridge"/> from the CloudEventBus),
/// <see cref="IEventsClient.OnTranscriptEvent"/> (agent-session
/// non-domain runtime event data, fanned out by
/// <see cref="Mohist.Server.Infrastructure.Events.SignalRTranscriptEventPublisher"/>
/// directly) and
/// <see cref="IEventsClient.OnTaskLogDelta"/> (ops task-log
/// runtime deltas, fanned out by
/// <see cref="Mohist.Server.Infrastructure.Events.SignalRTaskLogDeltaPublisher"/>
/// directly). All three are filtered by the per-connection
/// subscription set in <see cref="ConnectionSubscriptionRegistry"/>;
/// a client opts into a transcript / task-log type by including
/// it in its <see cref="SetSubscriptionsAsync"/> list. The task-log
/// channel additionally gates on a per-task
/// <c>(workflowRunId, taskId)</c> scope updated by
/// <see cref="SubscribeTaskLogAsync"/> /
/// <see cref="UnsubscribeTaskLogAsync"/>; the publisher skips
/// push to connections outside the scope (on-demand fan-out).
/// </para>
///
/// <para>
/// <b>Static vs dynamic split</b>. The bus has static subscribers
/// (registered at <c>StartAsync</c>, lifetime = process); this hub
/// manages dynamic, per-connection subscribers. The hub is the
/// <i>only</i> place where the user's runtime intent ("I want
/// issue events but not workflow log events") becomes bus-visible
/// state. Keeping the split clean here means the bus itself does
/// not have to know what a "user" is.
/// </para>
///
/// <para>
/// <b>Connection lifecycle</b>:
/// <list type="number">
///   <item><c>OnConnectedAsync</c>:
///     <list type="bullet">
///       <item>Register the connectionId in
///             <see cref="ConnectionSubscriptionRegistry"/>. The
///             initial subscription set is empty — that is the
///             expected default for a freshly opened tab. The
///             dispatcher will not push anything to a connection
///             whose set is empty, which is the correct behaviour
///             in the window between connect and the first
///             <see cref="SetSubscriptionsAsync"/> call.</item>
///       <item>Capture the <c>?projectId=</c> query-string value
///             as the connection's declared project affinity. The
///             dispatcher uses this together with
///             <c>extensions["projectid"]</c> on incoming
///             CloudEvents to gate project-scoped delivery (see
///             <c>UserNotificationDispatcher</c>). A missing value
///             leaves the connection on type-only matching —
///             the right default for cross-project / admin tabs.
///             Affinity is transport-level state, not durable.</item>
///       <item>Best-effort replay the durable subscription set from
///             <see cref="IConnectionSubscriptionGrain"/> when
///             present. This is the replay-on-reconnect path. A new
///             connectionId (the usual case after a SignalR
///             reconnect that rotated the connection id) starts
///             with an empty stored set, and the Web is expected to
///             re-invoke <see cref="SetSubscriptionsAsync"/> from
///             its <c>onreconnected</c> callback.</item>
///     </list>
///   </item>
///   <item><c>OnDisconnectedAsync</c>: unregister from the registry.
///         The grain is left alone — the connection is gone, but
///         the user's subscription preference may survive a
///         reconnect, so the durable record stays.</item>
///   <item>Hub methods: <see cref="SubscribeAsync"/> /
///         <see cref="UnsubscribeAsync"/> / <see cref="SetSubscriptionsAsync"/>:
///         update both the registry (hot path) and the grain
///         (durable). The client's first
///         <see cref="SetSubscriptionsAsync"/> call after
///         <c>OnConnectedAsync</c> is the source of truth and
///         populates both at once; <see cref="SetSubscriptionsAsync"/>
///         is idempotent, so a re-invoke on reconnect is a safe
///         no-op when the durable grain is already in sync.</item>
/// </list>
/// </para>
/// </summary>
public sealed class MohistHub : Hub<IEventsClient>
{
    private readonly IGrainFactory _grains;
    private readonly ConnectionSubscriptionRegistry _registry;

    public MohistHub(IGrainFactory grains, ConnectionSubscriptionRegistry registry)
    {
        _grains = grains;
        _registry = registry;
    }

    public override async Task OnConnectedAsync()
    {
        // Hot-path registry. RegisterConnection always inserts
        // an empty subscription set. That empty set is the
        // expected initial state for a freshly opened tab — the
        // dispatcher (UserNotificationDispatcher) filters by
        // ShouldNotify, and ShouldNotify returns false for an
        // empty set, so no bus emit reaches the connection in
        // the window between connect and the first
        // SetSubscriptionsAsync call from the client.
        _registry.RegisterConnection(Context.ConnectionId);

        // Capture per-connection project affinity from the
        // SignalR query string. The Web's events-hub.ts sends
        // ?projectId=<id> on every connection / reconnect; we
        // mirror it in the registry so the dispatcher can apply
        // the gated project filter (see
        // UserNotificationDispatcher.ResolveTargetConnectionsAsync
        // and design.md D3). A missing / empty value normalises
        // to null — the connection keeps type-only matching
        // behaviour, which is the right default for cross-project
        // tabs and admin tooling.
        var query = Context.GetHttpContext()?.Request.Query;
        var projectId = query?["projectId"].ToString();
        _registry.SetProjectId(Context.ConnectionId, projectId);

        // Replay the durable subscription set on reconnect. The
        // grain is keyed by connectionId; a new connectionId
        // (the usual case after a SignalR reconnect) starts
        // with an empty stored set, and the Web is expected to
        // re-invoke SetSubscriptionsAsync from its
        // onreconnected callback. A grain lookup failure MUST
        // NOT block OnConnectedAsync — the connection is open
        // either way, and the client will repopulate both the
        // grain and the registry from its first
        // SetSubscriptionsAsync call.
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        try
        {
            var saved = await grain.GetSubscriptionsAsync();
            _registry.SetSubscriptions(Context.ConnectionId, saved);
        }
        catch
        {
            // Best-effort replay — empty default is the
            // documented initial state; the next
            // SetSubscriptionsAsync call from the client
            // populates both the grain and the registry.
            _registry.SetSubscriptions(Context.ConnectionId, Array.Empty<string>());
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.UnregisterConnection(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Set the connection's subscription list to exactly
    /// <paramref name="eventTypes"/>. Idempotent: a second call
    /// with the same list does not duplicate or shift registry
    /// entries. Populates the in-process
    /// <see cref="ConnectionSubscriptionRegistry"/> (hot path the
    /// dispatcher reads) and the durable
    /// <see cref="IConnectionSubscriptionGrain"/> (source of truth
    /// for replay-on-reconnect) together.
    /// </summary>
    public async Task SetSubscriptionsAsync(IReadOnlyList<string> eventTypes)
    {
        _registry.SetSubscriptions(Context.ConnectionId, eventTypes);
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        await grain.SetSubscriptionsAsync(eventTypes);
    }

    /// <summary>Add one event type to this connection's subscription set.</summary>
    public async Task SubscribeAsync(string eventType)
    {
        _registry.Subscribe(Context.ConnectionId, eventType);
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        await grain.SubscribeAsync(eventType);
    }

    /// <summary>Remove one event type from this connection's subscription set.</summary>
    public async Task UnsubscribeAsync(string eventType)
    {
        _registry.Unsubscribe(Context.ConnectionId, eventType);
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        await grain.UnsubscribeAsync(eventType);
    }

    /// <summary>
    /// Subscribe this connection to live task-log fan-out for the
    /// given <c>(workflowRunId, taskId)</c>. Updates the
    /// process-local <see cref="ConnectionSubscriptionRegistry"/> (the
    /// hot path <see cref="Mohist.Server.Infrastructure.Events.SignalRTaskLogDeltaPublisher"/>
    /// reads on every publish) plus the connection's
    /// <see cref="Mohist.Server.User.Grains.IConnectionSubscriptionGrain"/>
    /// (durable replay source).
    ///
    /// <para>
    /// The first call from a client also adds the canonical
    /// <see cref="TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType"/>
    /// to the connection's type-subscription set so the publisher's
    /// type filter passes. Subsequent calls are idempotent.
    /// </para>
    ///
    /// <para>
    /// <b>Reconnect</b>: the Web is expected to re-invoke this from
    /// its SignalR <c>onreconnected</c> handler. The connection
    /// starts with an empty scope on a fresh connection id, so a
    /// re-subscribe is required to keep receiving deltas after a
    /// reconnect that rotated the id. We deliberately do not store
    /// task-log scope in the durable grain to avoid carrying new
    /// grain state (see design Open Questions).
    /// </para>
    /// </summary>
    public async Task SubscribeTaskLogAsync(string workflowRunId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(taskId)) return;

        _registry.SubscribeTaskLog(Context.ConnectionId, workflowRunId, taskId);
        // Ensure the type filter also passes — a connection that
        // declared interest in a task but never added the
        // task-log.delta type would otherwise silently fail the
        // ShouldNotifyTaskLog gate.
        _registry.Subscribe(Context.ConnectionId, TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);

        // Mirror the type-subscription on the durable grain so
        // reconnect replay re-asserts the type. The task scope
        // itself is not durable — the Web re-asserts it on reconnect.
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        await grain.SubscribeAsync(TaskLogDeltaSubscription.TaskLogDeltaSubscriptionType);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Unsubscribe this connection from live task-log fan-out for
    /// the given <c>(workflowRunId, taskId)</c>. Removes the pair
    /// from the registry's task scope set; the type-subscription
    /// (added by <see cref="SubscribeTaskLogAsync"/>) is left in
    /// place because removing it would interfere with any other
    /// task the connection might still be watching.
    /// </summary>
    public Task UnsubscribeTaskLogAsync(string workflowRunId, string taskId)
    {
        if (string.IsNullOrWhiteSpace(workflowRunId) || string.IsNullOrWhiteSpace(taskId))
            return Task.CompletedTask;

        _registry.UnsubscribeTaskLog(Context.ConnectionId, workflowRunId, taskId);
        return Task.CompletedTask;
    }
}
