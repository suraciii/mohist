using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Hub;

[EventPush(Type = "com.mohist.*")]
public sealed class EventBridge : ICloudEventPushHandler
{
    private readonly IUserNotificationDispatcher _dispatcher;
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ILogger<EventBridge> _log;

    public EventBridge(
        IUserNotificationDispatcher dispatcher,
        IHubContext<MohistHub, IEventsClient> hub,
        ILogger<EventBridge> log)
    {
        _dispatcher = dispatcher;
        _hub = hub;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public async Task HandleAsync(CloudEvent cloudEvent, CancellationToken ct)
    {
        try
        {
            var targets = await _dispatcher
                .ResolveTargetConnectionsAsync(cloudEvent, ct)
                .ConfigureAwait(false);

            if (targets.Count == 0)
            {
                return;
            }

            var envelope = CloudEventEnvelope.From(cloudEvent);
            var eventName = cloudEvent.Type ?? envelope.Type;

            foreach (var connectionId in targets)
            {
                try
                {
                    await _hub.Clients
                        .Client(connectionId)
                        .OnEvent(eventName, envelope)
                        .WaitAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
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
        Type: evt.Type,
        Payload: evt.Data,
        Id: evt.Id,
        Source: evt.Source.ToString(),
        SpecVersion: evt.SpecVersion,
        Subject: evt.Subject,
        Time: evt.Time.ToString("o"),
        DataContentType: evt.DataContentType,
        Extensions: evt.Extensions.Count == 0
            ? null
            : evt.Extensions.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value));
}
