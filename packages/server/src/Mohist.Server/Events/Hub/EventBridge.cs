using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.Hub;

public sealed class EventBridge : IHostedService, IDisposable
{
    private readonly IEventBus _bus;
    private readonly IHubContext<MohistHub, IEventsClient> _hub;
    private readonly ILogger<EventBridge> _log;
    private readonly List<Action<object>> _handlers = [];
    private readonly string[] _eventTypes;

    public EventBridge(IEventBus bus, IHubContext<MohistHub, IEventsClient> hub, ILogger<EventBridge> log)
    {
        _bus = bus;
        _hub = hub;
        _log = log;
        _eventTypes = EventBusEventTypes.All;
    }

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var eventType in _eventTypes)
        {
            Action<object> handler = data =>
            {
                var projectId = ExtractProjectId(data);
                var group = $"project:{projectId ?? "global"}";
                _ = _hub.Clients.Group(group).OnEvent(eventType, data);
            };
            _bus.On(eventType, handler);
            _handlers.Add(handler);
        }
        _log.LogInformation("EventBridge subscribed to {Count} event types", _eventTypes.Length);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Unsubscribe();
    }

    private static string? ExtractProjectId(object data)
    {
        if (data is IProjectScoped scoped)
            return scoped.ProjectId;

        try
        {
            var json = JsonSerializer.SerializeToElement(data);
            if (json.TryGetProperty("projectId", out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            if (json.TryGetProperty("ProjectId", out var prop2) && prop2.ValueKind == JsonValueKind.String)
                return prop2.GetString();
        }
        catch { }

        return null;
    }

    private void Unsubscribe()
    {
        for (var i = 0; i < _eventTypes.Length && i < _handlers.Count; i++)
            _bus.Off(_eventTypes[i], _handlers[i]);
        _handlers.Clear();
    }
}
