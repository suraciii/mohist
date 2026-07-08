using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.UnitTests.SystemSpecs;

public class EventBusTests
{
    [Fact]
    public async Task PublishAsync_NoSubscriber_DoesNotThrow()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("orphan"),
            type: "test.orphan",
            source: "test://orphan");
    }

    [Fact]
    public async Task PublishAsync_WithSubscriber_HandlerReceivesEnvelope()
    {
        var received = new Queue<CloudEvent>();
        var subs = new List<Subscription>
        {
            new("test.greeting", new RecordingHandler(
                filter: _ => true,
                onEvent: e => received.Enqueue(e)),
                DispatchDynamic),
        };
        var bus = new InMemoryEventBus(subs, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting");

        var match = received.Single(e => e.Type == "test.greeting");
        Assert.NotNull(match.Data);
    }

    [Fact]
    public async Task PublishAsync_FilteredOut_HandlerNotInvoked()
    {
        var received = new Queue<CloudEvent>();
        var subs = new List<Subscription>
        {
            new("test.greeting", new RecordingHandler(
                filter: e => false,
                onEvent: e => received.Enqueue(e)),
                DispatchDynamic),
        };
        var bus = new InMemoryEventBus(subs, NullLogger<InMemoryEventBus>.Instance);

        await bus.PublishAsync(
            data: new TestPayload("hello"),
            type: "test.greeting",
            source: "test://greeting");

        Assert.Empty(received);
    }

    private sealed record TestPayload(string Message);

    private static Task DispatchDynamic(object handler, CloudEvent evt, CancellationToken ct)
    {
        var h = (ICloudEventHandler)handler;
        if (!h.Filter(evt)) return Task.CompletedTask;
        return h.HandleAsync(evt, ct);
    }

    [Subscription(Type = "test.greeting")]
    private sealed class RecordingHandler : ICloudEventHandler
    {
        private readonly Func<CloudEvent, bool> _filter;
        private readonly Action<CloudEvent> _onEvent;

        public RecordingHandler(Func<CloudEvent, bool> filter, Action<CloudEvent> onEvent)
        {
            _filter = filter;
            _onEvent = onEvent;
        }

        public bool Filter(CloudEvent evt) => _filter(evt);

        public Task HandleAsync(CloudEvent evt, CancellationToken ct)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }

        public Task OnEvent(CloudEvent evt)
        {
            _onEvent(evt);
            return Task.CompletedTask;
        }
    }
}
