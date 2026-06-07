using CloudNative.CloudEvents;
using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Hub;

/// <summary>
/// Bridges the in-process <see cref="IEventBus"/> to SignalR clients.
/// Each emit is serialized as a CloudEvents 1.0.2 JSON envelope and forwarded
/// to the project's <c>project:{projectId}</c> hub group. Subscribes by
/// <c>type</c> for every entry in <see cref="EventCatalog.All"/> so both the
/// legacy snake_case names and the new reverse-DNS names are forwarded.
/// </summary>
public sealed class EventBridge : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ILogger<EventBridge> _log;
    private readonly List<IDisposable> _subscriptions = new();

    public EventBridge(IEventBus bus, IHubContext<MohistHub, IEventsClient> hub, ILogger<EventBridge> log)
    {
        _bus = bus;
        _hub = hub;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var type in EventCatalog.All)
        {
            _subscriptions.Add(_bus.OnType(type, ForwardToHub));
        }
        _log.LogInformation("EventBridge subscribed to {Count} event types", _subscriptions.Count);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }

    private void ForwardToHub(CloudEvent cloudEvent)
    {
        try
        {
            var projectId = ExtractProjectId(cloudEvent);
            var group = $"project:{projectId ?? "global"}";
            var envelope = CloudEventEnvelope.From(cloudEvent);
            _ = _hub.Clients.Group(group).OnEvent(cloudEvent.Type ?? envelope.Type, envelope);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventBridge failed to forward {Type}", cloudEvent.Type);
        }
    }

    private static string? ExtractProjectId(CloudEvent evt)
    {
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr is { IsExtension: true, Name: "projectid" } && value is not null)
            {
                return value.ToString();
            }
        }
        return null;
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
