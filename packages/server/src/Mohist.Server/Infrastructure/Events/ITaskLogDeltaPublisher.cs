namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes a freshly persisted task-log increment to project-scoped native
/// event WebSocket connections. Task-log output remains separate from domain
/// events, and publisher failure never affects authoritative persistence.
/// </summary>
public interface ITaskLogDeltaPublisher
{
    Task PublishAsync(TaskLogDeltaEnvelope envelope, CancellationToken ct = default);
}
