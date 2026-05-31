namespace Mohist.Server.Infrastructure.Events;

public interface IEventBus
{
    void On(string eventName, Action<object> handler);
    void Off(string eventName, Action<object> handler);
    void Emit(string eventName, object data);
}
