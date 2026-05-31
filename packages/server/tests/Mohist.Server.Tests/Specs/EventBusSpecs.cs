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
    public async Task Emit_WithSubscriber_ReceivesEvent()
    {
        var received = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _bus.On("test", data => received.SetResult(data));

        _bus.Emit("test", new { msg = "hello" });

        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("{\"msg\":\"hello\"}", System.Text.Json.JsonSerializer.Serialize(data));
    }

    [Fact]
    public void Emit_NoSubscriber_DoesNotThrow()
    {
        _bus.Emit("orphan", new { x = 1 });
    }

    [Fact]
    public async Task Off_RemovesSubscriber()
    {
        var count = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<object> handler = _ =>
        {
            Interlocked.Increment(ref count);
            first.TrySetResult();
        };
        _bus.On("counter", handler);
        _bus.Emit("counter", new { });
        await first.Task.WaitAsync(TimeSpan.FromSeconds(1));
        _bus.Off("counter", handler);
        _bus.Emit("counter", new { });
        await Task.Delay(50);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Emit_MultipleSubscribers_AllReceive()
    {
        var a = 0;
        var b = 0;
        var aReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bus.On("multi", _ =>
        {
            Interlocked.Increment(ref a);
            aReceived.SetResult();
        });
        _bus.On("multi", _ =>
        {
            Interlocked.Increment(ref b);
            bReceived.SetResult();
        });

        _bus.Emit("multi", new { });

        await Task.WhenAll(aReceived.Task, bReceived.Task).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Fact]
    public async Task Emit_DifferentEventTypes_Isolated()
    {
        var receivedA = false;
        var receivedB = false;
        var aReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bus.On("A", _ =>
        {
            receivedA = true;
            aReceived.SetResult();
        });
        _bus.On("B", _ => receivedB = true);

        _bus.Emit("A", new { });

        await aReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.True(receivedA);
        Assert.False(receivedB);
    }

    [Fact]
    public void Emit_SlowSubscriber_DoesNotBlockCaller()
    {
        var release = new ManualResetEventSlim(false);
        _bus.On("slow", _ => release.Wait());

        _bus.Emit("slow", new { });

        release.Set();
    }
}
