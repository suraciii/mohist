using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Events.WebSocket;

[EventPush(Type = "com.mohist.*")]
public sealed class EventSocketDomainBridge(EventWebSocketRegistry registry) : ICloudEventPushHandler
{
    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) =>
        registry.PublishDomainAsync(evt, ct);
}
