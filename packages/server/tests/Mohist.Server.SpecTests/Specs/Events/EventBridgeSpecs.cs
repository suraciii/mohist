using Mohist.Server.SpecTests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.TestSupport;

namespace Mohist.Server.SpecTests.Specs.Events;

public class EventBridgeSpecs
{
    [Fact]
    public async Task EventBridge_ReverseDnsStageStarted_ConnectionSubscribedToReverseDnsName_ReceivesEnvelope()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-reverse-dns");
        registry.Subscribe("conn-reverse-dns", EventCatalog.ReverseDns.StageStarted);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(BuildEvent(EventCatalog.ReverseDns.StageStarted, "/mohist/agent-session/sess-1"), CancellationToken.None);

        var message = Assert.Single(hub.Messages);
        Assert.Equal("conn-reverse-dns", message.ConnectionId);
        Assert.Equal(EventCatalog.ReverseDns.StageStarted, message.EventName);
        var envelope = Assert.IsType<CloudEventEnvelope>(message.Data);
        Assert.Equal(EventCatalog.ReverseDns.StageStarted, envelope.Type);
    }

    [Fact]
    public async Task EventBridge_ReverseDnsStageStarted_ConnectionSubscribedToBothNames_Receives()
    {
        // The Web's canonical subscription list (built by useEventsConnection
        // and surfaced by T-007's EVENT_TYPES helper) contains every legacy
        // snake_case name AND every reverse-DNS name. When the bus emits a
        // reverse-DNS event, the connection receives it because its
        // subscription set contains the reverse-DNS name. The legacy name
        // in the same set is what keeps an unmigrated legacy producer's
        // events flowing.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-canonical");
        registry.SetSubscriptions("conn-canonical", new[]
        {
            "stage_changed",
            EventCatalog.ReverseDns.StageStarted,
        });

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(BuildEvent(EventCatalog.ReverseDns.StageStarted, "/mohist/agent-session/sess-1"), CancellationToken.None);

        var message = Assert.Single(hub.Messages);
        Assert.Equal("conn-canonical", message.ConnectionId);
        var envelope = Assert.IsType<CloudEventEnvelope>(message.Data);
        Assert.Equal(EventCatalog.ReverseDns.StageStarted, envelope.Type);
    }

    [Fact]
    public async Task EventBridge_ReverseDnsStageStarted_UnsubscribedConnection_DoesNotReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-empty");
        registry.RegisterConnection("conn-other");

        registry.Subscribe("conn-other", "com.mohist.issue.created");

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(BuildEvent(EventCatalog.ReverseDns.StageStarted, "/mohist/agent-session/sess-1"), CancellationToken.None);

        Assert.Empty(hub.Messages);
    }

    [Fact]
    public async Task EventBridge_ReverseDnsAgentSessionRuntimeBound_TwoSubscribedConnections_BothReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.Subscribe("conn-A", EventCatalog.ReverseDns.AgentSessionRuntimeBound);
        registry.Subscribe("conn-B", EventCatalog.ReverseDns.AgentSessionRuntimeBound);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(BuildEvent(EventCatalog.ReverseDns.AgentSessionRuntimeBound, "/mohist/agent-session/sess-1"), CancellationToken.None);

        Assert.Equal(2, hub.Messages.Count);
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-A");
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-B");
    }

    private static EventBridge BuildBridge(ConnectionSubscriptionRegistry registry, IHubContext<MohistHub, IEventsClient> hub) =>
        new(new UserNotificationDispatcher(registry), hub, NullLogger<EventBridge>.Instance);

    private static CloudEvent BuildEvent(string type, string source) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.RelativeOrAbsolute),
            type: type,
            time: TestTime.UtcNow,
            data: null);

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
