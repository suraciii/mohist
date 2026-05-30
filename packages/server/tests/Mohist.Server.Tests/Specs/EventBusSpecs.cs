using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class EventBusSpecs
{
    private readonly InMemoryEventBus _bus;

    public EventBusSpecs()
    {
        _bus = new InMemoryEventBus(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
    }

    [Fact]
    public void Emit_WithSubscriber_ReceivesEvent()
    {
        var received = new List<object>();
        _bus.On("test", data => received.Add(data));

        _bus.Emit("test", new { msg = "hello" });

        Assert.Single(received);
        Assert.Equal("{\"msg\":\"hello\"}", System.Text.Json.JsonSerializer.Serialize(received[0]));
    }

    [Fact]
    public void Emit_NoSubscriber_DoesNotThrow()
    {
        _bus.Emit("orphan", new { x = 1 });
    }

    [Fact]
    public void Off_RemovesSubscriber()
    {
        var count = 0;
        Action<object> handler = _ => count++;
        _bus.On("counter", handler);
        _bus.Emit("counter", new { });
        _bus.Off("counter", handler);
        _bus.Emit("counter", new { });

        Assert.Equal(1, count);
    }

    [Fact]
    public void Emit_MultipleSubscribers_AllReceive()
    {
        var a = 0;
        var b = 0;
        _bus.On("multi", _ => a++);
        _bus.On("multi", _ => b++);

        _bus.Emit("multi", new { });

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public void Emit_DifferentEventTypes_Isolated()
    {
        var receivedA = false;
        var receivedB = false;
        _bus.On("A", _ => receivedA = true);
        _bus.On("B", _ => receivedB = true);

        _bus.Emit("A", new { });

        Assert.True(receivedA);
        Assert.False(receivedB);
    }
}
