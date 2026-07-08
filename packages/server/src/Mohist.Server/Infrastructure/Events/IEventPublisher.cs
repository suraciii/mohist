namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Appends one durable event row per call. Publish is write-only: it does not
/// invoke handlers. A separate dispatcher (planned) reads undelivered rows and
/// fans them out to <see cref="ICloudEventHandler"/> subscriptions.
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
