using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs;

public class EventBusSpecs
{
    private readonly InMemoryEventBus _bus;

    public EventBusSpecs()
    {
        _bus = new InMemoryEventBus(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Emit_WithSubscriber_ReceivesEvent()
    {
        object? received = null;
        _bus.On("test", data => received = data);

        _bus.Emit("test", new { msg = "hello" });

        Assert.NotNull(received);
        Assert.Equal("{\"msg\":\"hello\"}", System.Text.Json.JsonSerializer.Serialize(received));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Emit_NoSubscriber_DoesNotThrow()
    {
        _bus.Emit("orphan", new { x = 1 });
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Off_RemovesSubscriber()
    {
        var count = 0;
        Action<object> handler = _ => Interlocked.Increment(ref count);
        _bus.On("counter", handler);
        _bus.Emit("counter", new { });
        _bus.Off("counter", handler);
        _bus.Emit("counter", new { });

        Assert.Equal(1, count);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Emit_MultipleSubscribers_AllReceive()
    {
        var a = 0;
        var b = 0;
        _bus.On("multi", _ => Interlocked.Increment(ref a));
        _bus.On("multi", _ => Interlocked.Increment(ref b));

        _bus.Emit("multi", new { });

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
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

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Emit_SlowSubscriber_DoesBlockCaller()
    {
        var handlerCalled = false;
        var release = new ManualResetEventSlim(false);
        _bus.On("slow", _ =>
        {
            release.Wait();
            handlerCalled = true;
        });

        var emitTask = Task.Run(() => _bus.Emit("slow", new { }));
        release.Set();
        await emitTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(handlerCalled);
    }
}
