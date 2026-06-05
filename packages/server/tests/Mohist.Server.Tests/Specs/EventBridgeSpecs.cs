using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Xunit;

namespace Mohist.Server.Tests.Specs;

public class EventBridgeSpecs
{
    [Fact]
    public async Task EventBusEvent_ForProjectScopedPayload_IsSentToProjectGroup()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(bus, hub, NullLogger<EventBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        bus.Emit("stage_changed", new StageChangedEvent(
            "project-1",
            "workflow-1",
            "Plan",
            "Running",
            "started",
            null,
            "2026-06-05T00:00:00.0000000Z"));

        var message = Assert.Single(hub.Messages);
        Assert.Equal("project:project-1", message.GroupName);
        Assert.Equal("stage_changed", message.EventName);
        Assert.IsType<StageChangedEvent>(message.Data);

        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EventBusEvent_WithoutProjectScope_IsSentToGlobalGroup()
    {
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(bus, hub, NullLogger<EventBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        bus.Emit("schedule_triggered", new { reason = "manual" });

        var message = Assert.Single(hub.Messages);
        Assert.Equal("project:global", message.GroupName);
        Assert.Equal("schedule_triggered", message.EventName);

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
            public IEventsClient Client(string connectionId) => throw new NotSupportedException();
            public IEventsClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
            public IEventsClient Group(string groupName) => new RecordingEventsClient(_context, groupName);
            public IEventsClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
            public IEventsClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
            public IEventsClient User(string userId) => throw new NotSupportedException();
            public IEventsClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        }

        private sealed class RecordingEventsClient : IEventsClient
        {
            private readonly RecordingHubContext _context;
            private readonly string _groupName;

            public RecordingEventsClient(RecordingHubContext context, string groupName)
            {
                _context = context;
                _groupName = groupName;
            }

            public Task OnEvent(string eventName, object? data)
            {
                _context.Messages.Add(new RecordedHubEvent(_groupName, eventName, data));
                return Task.CompletedTask;
            }
        }

        private sealed class NoopGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed record RecordedHubEvent(string GroupName, string EventName, object? Data);
}
