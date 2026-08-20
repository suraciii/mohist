namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes project-scoped raw transcript runtime events to native event
/// WebSocket connections. Transcript events remain separate from the domain
/// event bus and authoritative persisted transcript.
/// </summary>
public interface ITranscriptEventPublisher
{
    Task PublishAsync(string projectId, TranscriptEnvelope envelope, CancellationToken ct = default);
}
