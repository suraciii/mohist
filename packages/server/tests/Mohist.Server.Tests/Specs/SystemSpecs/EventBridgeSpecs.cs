using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.Tests.Support;

namespace Mohist.Server.Tests.Specs.SystemSpecs;

public class EventBridgeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task EventBusEvent_ForSubscribedConnection_IsPushedToThatClient()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ConnectionSubscriptionRegistry();
        var dispatcher = new UserNotificationDispatcher(registry);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(bus, dispatcher, hub, NullLogger<EventBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        // Two connections. Only the first one is subscribed to the
        // event type. The dispatcher should forward to that one
        // only.
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.Subscribe("conn-A", "stage_changed");

        bus.Emit("stage_changed", new StageChangedEvent(
            "project-1",
            "workflow-1",
            "Plan",
            "Running",
            "started",
            null,
            "2026-06-05T00:00:00.0000000Z"));

        var message = Assert.Single(hub.Messages);
        Assert.Equal("conn-A", message.ConnectionId);
        Assert.Equal("stage_changed", message.EventName);
        var envelope = Assert.IsType<CloudEventEnvelope>(message.Data);
        Assert.Equal("stage_changed", envelope.Type);
        Assert.Equal("1.0", envelope.SpecVersion);
        Assert.NotNull(envelope.Id);
        Assert.NotNull(envelope.Time);
        Assert.Equal("project-1", envelope.Extensions?["projectid"]);

        await bridge.StopAsync(CancellationToken.None);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Unit)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task EventBusEvent_ForUnsubscribedConnection_IsNotPushedToThatClient()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var registry = new ConnectionSubscriptionRegistry();
        var dispatcher = new UserNotificationDispatcher(registry);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(bus, dispatcher, hub, NullLogger<EventBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        registry.RegisterConnection("conn-A");
        // conn-A has no subscriptions — registry defaults to empty set

        bus.Emit("schedule_triggered", new { reason = "manual" });

        // No messages pushed because conn-A is not subscribed to
        // "schedule_triggered".
        Assert.Empty(hub.Messages);

        await bridge.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingHubContext : IHubContext<MohistHub, IEventsClient>
    {
        private readonly RecordingHubClients _clients;

        public RecordingHubContext()
        {
            _clients = new RecordingHubClients(this);
        }

        public List<RecordedHubEvent> Messages { get; } = [];
        public IHubClients<IEventsClient> Clients => _clients;
        public IGroupManager Groups { get; } = new NoopGroupManager();

        private sealed class RecordingHubClients : IHubClients<IEventsClient>
        {
            private readonly RecordingHubContext _context;

            public RecordingHubClients(RecordingHubContext context)
            {
                _context = context;
            }

            public IEventsClient All => throw new NotSupportedException();
            public IEventsClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IEventsClient Client(string connectionId) => new RecordingEventsClient(_context, connectionId);
            public IEventsClient Clients(IReadOnlyList<string> connectionIds) => new RecordingEventsClient(_context, "multi");
            public IEventsClient Group(string groupName) => throw new NotSupportedException();
            public IEventsClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IEventsClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IEventsClient User(string userId) => throw new NotSupportedException();
            public IEventsClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class RecordingEventsClient : IEventsClient
        {
            private readonly RecordingHubContext _context;
            private readonly string _connectionId;

            public RecordingEventsClient(RecordingHubContext context, string connectionId)
            {
                _context = context;
                _connectionId = connectionId;
            }

            public Task OnEvent(string eventName, object? data)
            {
                _context.Messages.Add(new RecordedHubEvent(_connectionId, eventName, data));
                return Task.CompletedTask;
            }
        }

        private sealed class NoopGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed record RecordedHubEvent(string ConnectionId, string EventName, object? Data);
}
