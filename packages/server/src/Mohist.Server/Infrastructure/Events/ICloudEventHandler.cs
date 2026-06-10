namespace Mohist.Server.Infrastructure.Events;

public interface ICloudEventHandler<TData> where TData : class
{
    bool Filter(CloudEvent<TData> evt);
    Task HandleAsync(CloudEvent<TData> evt, CancellationToken ct);
}

public interface ICloudEventHandler
{
    bool Filter(CloudEvent evt);
    Task HandleAsync(CloudEvent evt, CancellationToken ct);
}
