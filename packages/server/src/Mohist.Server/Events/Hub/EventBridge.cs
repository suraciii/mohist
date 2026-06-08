using CloudNative.CloudEvents;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Hub;

/// <summary>
/// Bridges the in-process <see cref="IEventBus"/> to SignalR clients.
/// Subscribes to every entry in <see cref="EventCatalog.All"/> as a
/// <b>static, permanent</b> bus subscription, then on each emit
/// asks the <see cref="IUserNotificationDispatcher"/> which
/// SignalR connections are interested in this event, and pushes
/// the envelope to those connection IDs via
/// <see cref="IHubClients{T}.Client(string)"/>.
///
/// <para>
/// <b>Why the bus has no <c>Unsubscribe</c> for us</b>. The
/// bridge's subscription is part of the program's source: it
/// lives in <c>StartAsync</c>, dies with the process. There is no
/// runtime scenario where "this bridge should stop receiving
/// events"; the question is "which events should reach which
/// connections", and that is a per-connection question, answered
/// by the dispatcher reading the
/// <see cref="Mohist.Server.User.Grains.IConnectionSubscriptionGrain"/>.
/// </para>
///
/// <para>
/// <b>Why the dispatcher instead of SignalR groups</b>. The
/// prior design fanned events out by
/// <c>_hub.Clients.Group("project:{projectId}").OnEvent(...)</c>:
/// every connection in a project group got every project event,
/// regardless of whether the user was on the issues board tab
/// (which wants issue events) or the workflow tab (which wants
/// workflow events). The dispatcher is the long-form fix: per
/// connection, per event type, opt in or out, with the durable
/// record held in the per-connection grain.
/// </para>
/// </summary>
public sealed class EventBridge : IHostedService
{
    private readonly IEventBus _bus;
    private readonly IUserNotificationDispatcher _dispatcher;
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ILogger<EventBridge> _log;

    public EventBridge(
        IEventBus bus,
        IUserNotificationDispatcher dispatcher,
        IHubContext<MohistHub, IEventsClient> hub,
        ILogger<EventBridge> log)
    {
        _bus = bus;
        _dispatcher = dispatcher;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Static, permanent subscription. The bus's Subscribe
        // method is fire-and-forget for the calling thread; the
        // handler is invoked on the dispatcher's worker. There is
        // no IDisposable / ISubscription to hold — the bus
        // interface deliberately has no Unsubscribe.
        foreach (var type in EventCatalog.All)
        {
            _bus.Subscribe(type, evt => _ = ForwardAsync(evt));
        }
        _log.LogInformation("EventBridge subscribed to {Count} event types", EventCatalog.All.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // No subscriptions to tear down. The bus's typed
        // subscriptions are permanent; the dispatcher and the
        // hub context are owned by the host and released as part
        // of the host's own Dispose. The .NET host's
        // ServicesStopConcurrently setting cannot race us here
        // because there is no per-instance list to iterate.
        return Task.CompletedTask;
    }

    private async Task ForwardAsync(CloudEvent cloudEvent)
    {
        try
        {
            // ① Ask the dispatcher: "which connections want this
            //    event?". The dispatcher reads the
            //    ConnectionSubscriptionRegistry, which is the
            //    process-local mirror of the per-connection grain
            //    state.
            var targets = await _dispatcher
                .ResolveTargetConnectionsAsync(cloudEvent, CancellationToken.None)
                .ConfigureAwait(false);

            if (targets.Count == 0)
            {
                return;
            }

            // ② Push the envelope to each target connection. The
            //    IHubContext.Clients.Client(connectionId) call is
            //    the standard SignalR out-of-hub push, used here
            //    because we are in a bus handler thread, not a hub
            //    method thread.
            var envelope = CloudEventEnvelope.From(cloudEvent);
            var eventName = cloudEvent.Type ?? envelope.Type;

            foreach (var connectionId in targets)
            {
                try
                {
                    await _hub.Clients
                        .Client(connectionId)
                        .OnEvent(eventName, envelope)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One bad connection should not poison the
                    // others. Log and continue.
                    _log.LogWarning(ex,
                        "EventBridge failed to forward {Type} to {ConnectionId}",
                        eventName, connectionId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventBridge failed to forward {Type}", cloudEvent.Type);
        }
    }
}

/// <summary>
/// Wire shape sent to the Web over SignalR. Carries the full CloudEvents
/// 1.0.2 envelope (id, source, type, subject, time, extensions, data) so
/// the Web can read structured routing metadata without a JSON round-trip.
/// </summary>
public sealed record CloudEventEnvelope(
    string Type,
    object? Payload,
    string Id,
    string Source,
    string SpecVersion,
    string? Subject,
    string? Time,
    string? DataContentType,
    Dictionary<string, object?>? Extensions)
{
    public static CloudEventEnvelope From(CloudEvent evt) => new(
        Type: evt.Type ?? string.Empty,
        Payload: evt.Data,
        Id: evt.Id ?? string.Empty,
        Source: evt.Source?.ToString() ?? string.Empty,
        SpecVersion: evt.SpecVersion?.VersionId ?? "1.0",
        Subject: evt.Subject,
        Time: evt.Time?.ToString("o"),
        DataContentType: evt.DataContentType,
        Extensions: CloudEventEnvelopeExtensions.BuildExtensions(evt));
}

internal static class CloudEventEnvelopeExtensions
{
    public static Dictionary<string, object?>? BuildExtensions(CloudEvent evt)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr is { IsExtension: true })
            {
                dict[attr.Name] = value;
            }
        }
        return dict.Count == 0 ? null : dict;
    }
}
