using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.SystemSpecs;

public class EventBridgeSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task HandleAsync_ForSubscribedConnection_IsPushedToThatClient()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.Subscribe("conn-A", "com.mohist.workflow.run.completed");

        var dispatcher = new UserNotificationDispatcher(registry);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(dispatcher, hub, NullLogger<EventBridge>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow/workflow-1", UriKind.RelativeOrAbsolute),
            type: "com.mohist.workflow.run.completed",
            time: TestTime.UtcNow,
            data: null,
            subject: null,
            extensions: new Dictionary<string, string>
            {
                ["projectid"] = "project-1",
                ["workflowrunid"] = "workflow-1",
            });

        await bridge.HandleAsync(evt, CancellationToken.None);

        var message = Assert.Single(hub.Messages);
        Assert.Equal("conn-A", message.ConnectionId);
        Assert.Equal("com.mohist.workflow.run.completed", message.EventName);
        var innerEnvelope = Assert.IsType<CloudEventEnvelope>(message.Data);
        Assert.Equal("com.mohist.workflow.run.completed", innerEnvelope.Type);
        Assert.Equal("1.0", innerEnvelope.SpecVersion);
        Assert.NotNull(innerEnvelope.Id);
        Assert.NotNull(innerEnvelope.Time);
        Assert.Equal("project-1", innerEnvelope.Extensions?["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task HandleAsync_ForUnsubscribedConnection_IsNotPushedToAnyClient()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");

        var dispatcher = new UserNotificationDispatcher(registry);
        var hub = new RecordingHubContext();
        var bridge = new EventBridge(dispatcher, hub, NullLogger<EventBridge>.Instance);

        var evt = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri("/mohist/workflow/workflow-1", UriKind.RelativeOrAbsolute),
            type: "com.mohist.workflow.run.paused",
            time: TestTime.UtcNow,
            data: null);

        await bridge.HandleAsync(evt, CancellationToken.None);

        Assert.Empty(hub.Messages);
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

            public Task OnTranscriptEvent(TranscriptEnvelope envelope)
            {
                _context.Messages.Add(new RecordedHubEvent(_connectionId, envelope?.Type ?? string.Empty, envelope));
                return Task.CompletedTask;
            }

            public Task OnTaskLogDelta(TaskLogDeltaEnvelope envelope)
            {
                _context.Messages.Add(new RecordedHubEvent(_connectionId, "task-log.delta", envelope));
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
