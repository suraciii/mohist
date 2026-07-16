using CloudNative.CloudEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.SpecTests.Support;
using Xunit;
using CloudEvent = Mohist.Server.Infrastructure.Events.CloudEvent;

namespace Mohist.Server.SpecTests.Specs.Events;

/// <summary>
/// End-to-end integration tests for the project-id isolation gate:
/// <see cref="EventBridge"/> → <see cref="UserNotificationDispatcher"/>
/// → per-connection routing via <see cref="ConnectionSubscriptionRegistry"/>.
/// These tests exercise the signal path the inbox hint takes when
/// the future dispatcher invokes <see cref="EventBridge.HandleAsync"/>;
/// unit-level coverage of the dispatcher's gate lives in
/// <c>UserNotificationDispatcherProjectFilterSpecs</c>.
/// </summary>
public class ProjectIsolationIntegrationSpecs
{
    private const string InboxHintType = "com.mohist.inbox.item-persisted";

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboxHint_ProjectA_ReachesProjectASession_NotProjectBSession()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");
        registry.Subscribe("conn-A", InboxHintType);
        registry.Subscribe("conn-B", InboxHintType);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(InboxHintType, "/mohist/inbox", new Dictionary<string, string>
            {
                ["projectid"] = "proj-A",
            }),
            CancellationToken.None);

        var projectAMessage = Assert.Single(hub.Messages);
        Assert.Equal("conn-A", projectAMessage.ConnectionId);
        Assert.Equal(InboxHintType, projectAMessage.EventName);
        var envelope = Assert.IsType<CloudEventEnvelope>(projectAMessage.Data);
        Assert.Equal(InboxHintType, envelope.Type);
        Assert.Equal("proj-A", envelope.Extensions?["projectid"]);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboxHint_ProjectB_ReachesProjectBSession_NotProjectASession()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");
        registry.Subscribe("conn-A", InboxHintType);
        registry.Subscribe("conn-B", InboxHintType);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(InboxHintType, "/mohist/inbox", new Dictionary<string, string>
            {
                ["projectid"] = "proj-B",
            }),
            CancellationToken.None);

        var message = Assert.Single(hub.Messages);
        Assert.Equal("conn-B", message.ConnectionId);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboxHint_EventWithoutProjectStamp_FallsBackToTypeOnlyMatching()
    {
        // Documented fallback: a CloudEvent without
        // extensions["projectid"] skips the project gate and
        // matches on event type alone. This is the deliberate
        // "blast-radius guard" — every existing non-projectid
        // event is byte-for-byte unchanged. In particular, a
        // malformed / older inbox hint (or any other event that
        // forgets to stamp the project) still reaches every
        // type-matched session regardless of their project
        // affinity, mirroring the pre-T-002 behaviour exactly.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");
        registry.Subscribe("conn-A", InboxHintType);
        registry.Subscribe("conn-B", InboxHintType);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(InboxHintType, "/mohist/inbox", extensions: null),
            CancellationToken.None);

        // Both connections receive the message — the gate is
        // inert when extensions["projectid"] is absent, so
        // type-only matching applies. This is the design.md D3
        // behaviour: "When either side is absent, behavior is
        // unchanged (type-only match)."
        Assert.Equal(2, hub.Messages.Count);
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-A");
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-B");
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboxHint_ReachesCrossProjectSession_WhenConnectionHasNoProjectAffinity()
    {
        // A connection that hasn't declared a projectId (cross-
        // project / admin tooling) keeps type-only matching and
        // therefore receives the hint regardless of which project
        // the hint is for. This is the intended behaviour — admin
        // tooling remains in scope even when project-scoped
        // routing is enabled for per-project tabs.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-cross");
        registry.SetProjectId("conn-A", "proj-A");
        // conn-cross deliberately has no project affinity.
        registry.Subscribe("conn-A", InboxHintType);
        registry.Subscribe("conn-cross", InboxHintType);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(InboxHintType, "/mohist/inbox", new Dictionary<string, string>
            {
                ["projectid"] = "proj-A",
            }),
            CancellationToken.None);

        var deliveredTo = hub.Messages.Select(m => m.ConnectionId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("conn-A", deliveredTo);
        Assert.Contains("conn-cross", deliveredTo);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task InboxHint_ManySessionsSubscribed_OnlyOwningProjectSessionsReceive()
    {
        // Mixed fan-out: 3 project-A sessions, 2 project-B
        // sessions, 1 cross-project session. The bus emits one
        // project-A inbox hint. Result: the 3 project-A + the
        // cross-project session receive; the 2 project-B do not.
        var registry = new ConnectionSubscriptionRegistry();
        foreach (var id in new[] { "conn-A1", "conn-A2", "conn-A3", "conn-B1", "conn-B2", "conn-cross" })
        {
            registry.RegisterConnection(id);
            registry.Subscribe(id, InboxHintType);
        }
        registry.SetProjectId("conn-A1", "proj-A");
        registry.SetProjectId("conn-A2", "proj-A");
        registry.SetProjectId("conn-A3", "proj-A");
        registry.SetProjectId("conn-B1", "proj-B");
        registry.SetProjectId("conn-B2", "proj-B");
        // conn-cross deliberately has no project affinity.

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(InboxHintType, "/mohist/inbox", new Dictionary<string, string>
            {
                ["projectid"] = "proj-A",
            }),
            CancellationToken.None);

        var deliveredTo = hub.Messages.Select(m => m.ConnectionId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(4, deliveredTo.Count);
        Assert.Contains("conn-A1", deliveredTo);
        Assert.Contains("conn-A2", deliveredTo);
        Assert.Contains("conn-A3", deliveredTo);
        Assert.Contains("conn-cross", deliveredTo);
        Assert.DoesNotContain("conn-B1", deliveredTo);
        Assert.DoesNotContain("conn-B2", deliveredTo);
    }

    [Trait(Traits.Speed.Name, Traits.Speed.Service)]
    [Trait(Traits.Sut.Name, Traits.Sut.System)]
    [Fact]
    public async Task LegacyEvent_NoProjectStamp_ReachesAllAffinitizedSessions()
    {
        // Regression: a legacy CloudEvent without extensions["projectid"]
        // reaches every project-affinitized session that subscribed to
        // the type. The dispatcher gate is inert when the extension is
        // absent — type-only matching applies, byte-for-byte unchanged.
        const string LegacyType = "com.mohist.workflow.stage.started";
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");
        registry.Subscribe("conn-A", LegacyType);
        registry.Subscribe("conn-B", LegacyType);

        var hub = new RecordingHubContext();
        var bridge = BuildBridge(registry, hub);

        await bridge.HandleAsync(
            BuildEvent(LegacyType, "/mohist/agent-session/sess-1", extensions: null),
            CancellationToken.None);

        Assert.Equal(2, hub.Messages.Count);
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-A");
        Assert.Contains(hub.Messages, m => m.ConnectionId == "conn-B");
    }

    private static EventBridge BuildBridge(ConnectionSubscriptionRegistry registry, IHubContext<MohistHub, IEventsClient> hub) =>
        new(new UserNotificationDispatcher(registry), hub, NullLogger<EventBridge>.Instance);

    private static CloudEvent BuildEvent(string type, string source, IReadOnlyDictionary<string, string>? extensions) =>
        new(
            id: Guid.NewGuid().ToString(),
            source: new Uri(source, UriKind.RelativeOrAbsolute),
            type: type,
            time: TestTime.UtcNow,
            data: null,
            extensions: extensions is null ? null : new Dictionary<string, string>(extensions, StringComparer.Ordinal));

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
