namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Publishes events to the in-process event bus.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(CloudEvent envelope, CancellationToken ct = default);

    Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default);
}
