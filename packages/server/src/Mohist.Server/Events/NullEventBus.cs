namespace Mohist.Server.Events;

public class NullEventBus : IEventBus
{
    public static readonly NullEventBus Instance = new();

    public void On(string eventName, Action<object> handler) { }
    public void Off(string eventName, Action<object> handler) { }
    public void Emit(string eventName, object data) { }
}
