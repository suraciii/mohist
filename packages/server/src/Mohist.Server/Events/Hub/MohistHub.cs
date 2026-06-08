using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.User.Grains;

namespace Mohist.Server.Events.Hub;

public interface IEventsClient
{
    /// <summary>
    /// Receive an event from the bus. <paramref name="eventName"/> is the
    /// CloudEvents <c>type</c> for back-compat; <paramref name="data"/>
    /// carries a <see cref="CloudEventEnvelope"/> with the full CloudEvents
    /// 1.0.2 attributes (id, source, type, subject, time, extensions, data).
    /// New Web code should read from <c>envelope</c> in <paramref name="data"/>.
    /// </summary>
    Task OnEvent(string eventName, object? data);
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
///       <item>Add the connection to <c>project:global</c> and
///             <c>project:{projectId}</c> SignalR groups for
///             back-compat (older clients used these groups for
///             broadcast).</item>
///       <item>Register the connectionId in
///             <see cref="ConnectionSubscriptionRegistry"/>.</item>
///       <item>Fetch the durable subscription set from
///             <see cref="IConnectionSubscriptionGrain"/> and
///             re-apply it to the registry (replay-on-reconnect).</item>
///     </list>
///   </item>
///   <item><c>OnDisconnectedAsync</c>: remove from groups and
///         unregister from the registry. The grain is left
///         alone — the connection is gone, but the user's
///         subscription preference may survive a reconnect, so the
///         durable record stays.</item>
///   <item>Hub methods: <see cref="SubscribeAsync"/> /
///         <see cref="UnsubscribeAsync"/> / <see cref="SetSubscriptionsAsync"/>:
///         update both the registry (hot path) and the grain
///         (durable).</item>
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
        // Back-compat group memberships. New clients prefer
        // Subscribe-based filtering; the SignalR groups are kept
        // for any client that still relies on broadcast.
        await Groups.AddToGroupAsync(Context.ConnectionId, "project:global");
        var projectId = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
        if (!string.IsNullOrEmpty(projectId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");
        }

        // Hot-path registry. The dispatcher will see this
        // connection on its next emit.
        _registry.RegisterConnection(Context.ConnectionId);

        // Replay the durable subscription set on reconnect. The
        // grain is keyed by connectionId; a new connectionId (the
        // usual case after SignalR reconnect) starts with an empty
        // set, and the Web UI is expected to call SetSubscriptions
        // on tab open.
        var grain = _grains.GetGrain<IConnectionSubscriptionGrain>(Context.ConnectionId);
        try
        {
            var saved = await grain.GetSubscriptionsAsync();
            _registry.SetSubscriptions(Context.ConnectionId, saved);
        }
        catch
        {
            // Best-effort replay — fresh connection with empty
            // default is fine; the next SetSubscriptions call
            // from the client will populate both the grain and
            // the registry.
            _registry.SetSubscriptions(Context.ConnectionId, Array.Empty<string>());
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.UnregisterConnection(Context.ConnectionId);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "project:global");
        var projectId = Context.GetHttpContext()?.Request.Query["projectId"].ToString();
        if (!string.IsNullOrEmpty(projectId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project:{projectId}");
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Set the connection's subscription list to exactly
    /// <paramref name="eventTypes"/>. Idempotent.
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
}
