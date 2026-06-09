using CloudNative.CloudEvents;

namespace Mohist.Server.Infrastructure.Events;

public interface IEventBus
{
    void Emit(CloudEvent cloudEvent);

    Task EmitAsync(CloudEvent cloudEvent, CancellationToken ct = default);

    void Subscribe(string eventType, Func<CloudEvent, Task> handler);

    void Subscribe(Func<CloudEvent, bool> filter, Func<CloudEvent, Task> handler);

    void RegisterHandlerInterfaces(
        IReadOnlyDictionary<string, Type> eventTypeToHandlerInterface);
}
