using CloudNative.CloudEvents;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class EventBusSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Subscribe_FilterByType_HandlerReceivesMatchingEvent()
    {
        var bus = new InMemoryEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
        var matchingReceived = new List<CloudEvent>();
        var otherReceived = new List<CloudEvent>();
        bus.Subscribe("matching", evt => { matchingReceived.Add(evt); return Task.CompletedTask; });
        bus.Subscribe("other", evt => { otherReceived.Add(evt); return Task.CompletedTask; });

        var matching = CloudEventFactory.Create(
            type: "matching",
            source: new Uri("about:blank", UriKind.Absolute));
        bus.Emit(matching);

        Assert.Single(matchingReceived);
        Assert.Empty(otherReceived);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public void Subscribe_NoSubscriber_DoesNotThrow()
    {
        var bus = new InMemoryEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
        var orphan = CloudEventFactory.Create(
            type: "orphan",
            source: new Uri("about:blank", UriKind.Absolute));
        bus.Emit(orphan);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Emit_WithSubscriber_WaitsForHandler()
    {
        var bus = new InMemoryEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
        var handlerCompleted = false;
        bus.Subscribe("awaitable", _ =>
        {
            handlerCompleted = true;
            return Task.CompletedTask;
        });

        var evt = CloudEventFactory.Create(
            type: "awaitable",
            source: new Uri("about:blank", UriKind.Absolute));
        await bus.EmitAsync(evt);

        Assert.True(handlerCompleted);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task Emit_MultipleSubscribers_AllReceive()
    {
        var bus = new InMemoryEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
        var a = 0;
        var b = 0;
        bus.Subscribe("multi", _ => { Interlocked.Increment(ref a); return Task.CompletedTask; });
        bus.Subscribe("multi", _ => { Interlocked.Increment(ref b); return Task.CompletedTask; });

        var evt = CloudEventFactory.Create(
            type: "multi",
            source: new Uri("about:blank", UriKind.Absolute));
        await bus.EmitAsync(evt);

        Assert.Equal(1, a);
        Assert.Equal(1, b);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task EmitAsync_SlowSubscriber_AwaitsHandler()
    {
        var bus = new InMemoryEventBus(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryEventBus>.Instance);
        var handlerStarted = new ManualResetEventSlim(false);
        var release = new TaskCompletionSource();
        bus.Subscribe("slow", async _ =>
        {
            handlerStarted.Set();
            await release.Task;
        });

        var evt = CloudEventFactory.Create(
            type: "slow",
            source: new Uri("about:blank", UriKind.Absolute));

        var emitTask = bus.EmitAsync(evt);
        Assert.True(handlerStarted.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(emitTask.IsCompleted);

        release.SetResult();
        await emitTask.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
