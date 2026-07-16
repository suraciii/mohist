using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Xunit;
using Mohist.Server.SpecTests.Support;

namespace Mohist.Server.SpecTests.Specs.Events;

public class TranscriptEventPublisherSpecs
{
    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_SubscribedConnection_DeliversEnvelopeOnTranscriptChannel()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-transcript");
        registry.Subscribe("conn-transcript", "coder_text_chunk");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        var envelope = NewTranscriptEnvelope(type: "coder_text_chunk", text: "hello");
        await publisher.PublishAsync(envelope);

        var message = Assert.Single(hub.TranscriptMessages);
        Assert.Equal("conn-transcript", message.ConnectionId);
        Assert.Equal("coder_text_chunk", message.Envelope.Type);
        Assert.Equal("hello", message.Envelope.Payload.GetProperty("text").GetString());
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_UnsubscribedConnection_DoesNotReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-not-subscribed");
        registry.RegisterConnection("conn-subscribed");
        registry.Subscribe("conn-subscribed", "coder_text_chunk");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await publisher.PublishAsync(NewTranscriptEnvelope(type: "coder_text_chunk", text: "hi"));

        var message = Assert.Single(hub.TranscriptMessages);
        Assert.Equal("conn-subscribed", message.ConnectionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_ConnectionSubscribedToDifferentType_DoesNotReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-other");
        registry.Subscribe("conn-other", "ralph_task_update");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await publisher.PublishAsync(NewTranscriptEnvelope(type: "coder_text_chunk", text: "no route"));

        Assert.Empty(hub.TranscriptMessages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_MultipleSubscribedConnections_AllReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.RegisterConnection("conn-C");
        registry.Subscribe("conn-A", "ralph_task_update");
        registry.Subscribe("conn-B", "ralph_task_update");
        registry.Subscribe("conn-C", "coder_text_chunk");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await publisher.PublishAsync(NewTranscriptEnvelope(type: "ralph_task_update", text: "task-1"));

        Assert.Equal(2, hub.TranscriptMessages.Count);
        Assert.All(hub.TranscriptMessages, m => Assert.Equal("ralph_task_update", m.Envelope.Type));
        Assert.Contains(hub.TranscriptMessages, m => m.ConnectionId == "conn-A");
        Assert.Contains(hub.TranscriptMessages, m => m.ConnectionId == "conn-B");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_NoSubscribers_DoesNotThrow()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await publisher.PublishAsync(NewTranscriptEnvelope(type: "coder_text_chunk", text: "orphan"));

        Assert.Empty(hub.TranscriptMessages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_EmptyType_DoesNotDeliver()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-empty-type");
        registry.Subscribe("conn-empty-type", "coder_text_chunk");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        var envelope = NewTranscriptEnvelope(type: string.Empty, text: "x");
        await publisher.PublishAsync(envelope);

        Assert.Empty(hub.TranscriptMessages);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_NullEnvelope_Throws()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishAsync(null!));
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.AgentSession)]
    [Fact]
    public async Task PublishAsync_TranscriptEnvelope_DoesNotLeakOnTaskLogChannel()
    {
        // Channel-separation contract: the transcript publisher
        // forwards envelopes only via OnTranscriptEvent. A task-log
        // client must NEVER see a transcript envelope as a task-log
        // delta — the two channels are physically separate.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", "coder_text_chunk");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTranscriptEventPublisher(hub, registry, NullLogger<SignalRTranscriptEventPublisher>.Instance);

        await publisher.PublishAsync(NewTranscriptEnvelope(type: "coder_text_chunk", text: "hi"));

        Assert.Single(hub.TranscriptMessages);
        Assert.Empty(hub.TaskLogDeltaMessages);
    }

    private static TranscriptEnvelope NewTranscriptEnvelope(string type, string text)
    {
        var payload = JsonSerializer.SerializeToElement(new { text });
        return new TranscriptEnvelope(
            Id: Guid.NewGuid().ToString(),
            SessionId: $"sess-{Guid.NewGuid():N}",
            AgentSessionId: "acp-1",
            Sequence: 1,
            Type: type,
            Payload: payload,
            CreatedAt: TestTime.UtcDateTime.ToString("o"));
    }

    private sealed class RecordingHubContext : IHubContext<MohistHub, IEventsClient>
    {
        private readonly RecordingHubClients _clients;

        public RecordingHubContext()
        {
            _clients = new RecordingHubClients(this);
        }

        public List<RecordedTranscriptEvent> TranscriptMessages { get; } = [];
        public List<RecordedTaskLogDelta> TaskLogDeltaMessages { get; } = [];
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

            public Task OnEvent(string eventName, object? data) => Task.CompletedTask;

            public Task OnTranscriptEvent(TranscriptEnvelope envelope)
            {
                _context.TranscriptMessages.Add(new RecordedTranscriptEvent(_connectionId, envelope));
                return Task.CompletedTask;
            }

            public Task OnTaskLogDelta(TaskLogDeltaEnvelope envelope)
            {
                _context.TaskLogDeltaMessages.Add(new RecordedTaskLogDelta(_connectionId, envelope));
                return Task.CompletedTask;
            }
        }

        private sealed class NoopGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    private sealed record RecordedTranscriptEvent(string ConnectionId, TranscriptEnvelope Envelope);
    private sealed record RecordedTaskLogDelta(string ConnectionId, TaskLogDeltaEnvelope Envelope);
}
