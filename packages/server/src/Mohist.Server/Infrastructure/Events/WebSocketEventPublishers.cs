using Mohist.Server.Events.WebSocket;

namespace Mohist.Server.Infrastructure.Events;

public sealed class WebSocketTranscriptEventPublisher(EventWebSocketRegistry registry) : ITranscriptEventPublisher
{
    public Task PublishAsync(string projectId, TranscriptEnvelope envelope, CancellationToken ct = default) =>
        registry.PublishTranscriptAsync(projectId, envelope, ct);
}

public sealed class WebSocketTaskLogDeltaPublisher(EventWebSocketRegistry registry) : ITaskLogDeltaPublisher
{
    public Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default) =>
        registry.PublishTaskLogAsync(envelope, ct);
}
