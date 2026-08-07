using Mohist.Server.SpecTests.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Hub;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Events;

public class SignalRTaskLogDeltaPublisherSpecs
{
    private const string TaskLogType = "task-log.delta";

    [Fact]
    public async Task PublishAsync_SubscribedConnection_DeliversDeltaOnTaskLogChannel()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SetProjectId("conn-A", "proj-A");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        var envelope = NewEnvelope("wf-1", "task-1", workId: "w-1");
        await publisher.PublishAsync(envelope);

        var message = Assert.Single(hub.TaskLogDeltaMessages);
        Assert.Equal("conn-A", message.ConnectionId);
        Assert.Equal("w-1", message.Envelope.WorkId);
        Assert.Equal("task-1", message.Envelope.TaskId);
    }

    [Fact]
    public async Task PublishAsync_UnsubscribeByTask_DoesNotReceive()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-2");
        registry.SetProjectId("conn-A", "proj-A");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        // The connection only asked for task-2 — a delta for
        // task-1 in the same run must NOT be delivered.
        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        Assert.Empty(hub.TaskLogDeltaMessages);
    }

    [Fact]
    public async Task PublishAsync_OnlyInterestedClientsReceive_NotOthersOnSameRun()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.RegisterConnection("conn-C");

        registry.Subscribe("conn-A", TaskLogType);
        registry.Subscribe("conn-B", TaskLogType);
        registry.Subscribe("conn-C", TaskLogType);
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-A");
        registry.SetProjectId("conn-C", "proj-A");

        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-B", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-C", "wf-1", "task-2");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        Assert.Equal(2, hub.TaskLogDeltaMessages.Count);
        Assert.Contains(hub.TaskLogDeltaMessages, m => m.ConnectionId == "conn-A");
        Assert.Contains(hub.TaskLogDeltaMessages, m => m.ConnectionId == "conn-B");
        Assert.DoesNotContain(hub.TaskLogDeltaMessages, m => m.ConnectionId == "conn-C");
    }

    [Fact]
    public async Task PublishAsync_NoSubscribers_DoesNotThrowAndDoesNotProduceFanOut()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SetProjectId("conn-A", "proj-A");
        // Connection is registered but neither subscribed to the
        // task-log type nor declared interest in any task.

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        Assert.Empty(hub.TaskLogDeltaMessages);
    }

    [Fact]
    public async Task PublishAsync_SubscribedTypeMissing_DoesNotReceive()
    {
        // Type filter alone is insufficient — the connection
        // must BOTH have the type-subscription AND have a scope
        // pair containing the delta's (workflowRunId, taskId).
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SetProjectId("conn-A", "proj-A");
        // No type-subscription registered — the type filter
        // rejects the connection before the scope filter is
        // even consulted.

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        Assert.Empty(hub.TaskLogDeltaMessages);
    }

    [Fact]
    public async Task PublishAsync_PerSendThrows_OtherClientsStillReceive()
    {
        // Per-send failure isolation: one connection's send that
        // throws must not abort fan-out to remaining connections
        // and must not propagate the throw out to the caller.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.Subscribe("conn-A", TaskLogType);
        registry.Subscribe("conn-B", TaskLogType);
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-A");
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-B", "wf-1", "task-1");

        var hub = new RecordingHubContext { FailOnConnectionId = "conn-A" };
        var log = new List<string>();
        var logger = new ListLogger(log);
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, logger);

        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        // conn-B still received the delta even though conn-A's
        // send failed.
        var message = Assert.Single(hub.TaskLogDeltaMessages);
        Assert.Equal("conn-B", message.ConnectionId);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task PublishAsync_NullEnvelope_Throws()
    {
        var registry = new ConnectionSubscriptionRegistry();
        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishAsync(null!));
    }

    [Fact]
    public async Task PublishAsync_NullTaskId_DoesNotFanOut()
    {
        // When workId → taskId resolution fails (e.g. work item
        // not owned by a workflow run), the envelope carries a
        // null taskId. The publisher treats that as "no scope
        // can match" — no fan-out, no throw.
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.Subscribe("conn-A", TaskLogType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SetProjectId("conn-A", "proj-A");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        var envelope = new TaskLogDeltaEnvelope(
            OwnerKind: "workflow",
            OwnerId: "wf-1",
            ProjectId: "proj-A",
            WorkId: "w-1",
            TaskId: null,
            Entries: Array.Empty<TaskLogDeltaEntry>(),
            Truncated: false);
        await publisher.PublishAsync(envelope);

        Assert.Empty(hub.TaskLogDeltaMessages);
    }

    private static TaskLogDeltaEnvelope NewEnvelope(string workflowRunId, string taskId, string workId)
    {
        var now = TestTime.UtcNow;
        return new TaskLogDeltaEnvelope(
            OwnerKind: "workflow",
            OwnerId: workflowRunId,
            ProjectId: "proj-A",
            WorkId: workId,
            TaskId: taskId,
            Entries: new[]
            {
                new TaskLogDeltaEntry(Seq: 1, Timestamp: now, Source: "stdout", Text: "hello"),
            },
            Truncated: false);
    }

    [Fact]
    public async Task PublishAsync_ProjectScopedDelta_DeliversOnlyToMatchingProjectConnection()
    {
        var registry = new ConnectionSubscriptionRegistry();
        registry.RegisterConnection("conn-A");
        registry.RegisterConnection("conn-B");
        registry.Subscribe("conn-A", TaskLogType);
        registry.Subscribe("conn-B", TaskLogType);
        registry.SubscribeTaskLog("conn-A", "wf-1", "task-1");
        registry.SubscribeTaskLog("conn-B", "wf-1", "task-1");
        registry.SetProjectId("conn-A", "proj-A");
        registry.SetProjectId("conn-B", "proj-B");

        var hub = new RecordingHubContext();
        var publisher = new SignalRTaskLogDeltaPublisher(hub, registry, NullLogger<SignalRTaskLogDeltaPublisher>.Instance);

        await publisher.PublishAsync(NewEnvelope("wf-1", "task-1", "w-1"));

        var message = Assert.Single(hub.TaskLogDeltaMessages);
        Assert.Equal("conn-A", message.ConnectionId);
    }

    private sealed class RecordingHubContext : IHubContext<MohistHub, IEventsClient>
    {
        public List<RecordedTaskLogDelta> TaskLogDeltaMessages { get; } = [];
        public List<RecordedTranscriptEvent> TranscriptMessages { get; } = [];
        public List<RecordedHubEvent> EventMessages { get; } = [];
        public string? FailOnConnectionId { get; set; }

        public IHubClients<IEventsClient> Clients => new RecordingHubClients(this);
        public IGroupManager Groups => new NoopGroupManager();

        private sealed class RecordingHubClients : IHubClients<IEventsClient>
        {
            private readonly RecordingHubContext _context;
            public RecordingHubClients(RecordingHubContext context) { _context = context; }
            public IEventsClient All => throw new NotSupportedException();
            public IEventsClient AllExcept(IReadOnlyList<string> excluded) => throw new NotSupportedException();
            public IEventsClient Client(string connectionId) => new RecordingEventsClient(_context, connectionId);
            public IEventsClient Clients(IReadOnlyList<string> connectionIds) => new RecordingEventsClient(_context, "multi");
            public IEventsClient Group(string groupName) => throw new NotSupportedException();
            public IEventsClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => throw new NotSupportedException();
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
                MaybeFail();
                _context.EventMessages.Add(new RecordedHubEvent(_connectionId, eventName, data));
                return Task.CompletedTask;
            }

            public Task OnTranscriptEvent(TranscriptEnvelope envelope)
            {
                MaybeFail();
                _context.TranscriptMessages.Add(new RecordedTranscriptEvent(_connectionId, envelope));
                return Task.CompletedTask;
            }

            public Task OnTaskLogDelta(TaskLogDeltaEnvelope envelope)
            {
                MaybeFail();
                _context.TaskLogDeltaMessages.Add(new RecordedTaskLogDelta(_connectionId, envelope));
                return Task.CompletedTask;
            }

            private void MaybeFail()
            {
                if (_context.FailOnConnectionId == _connectionId)
                {
                    throw new InvalidOperationException($"simulated per-send failure for {_connectionId}");
                }
            }
        }

        private sealed class NoopGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default) => Task.CompletedTask;
        }
    }

    private sealed record RecordedTaskLogDelta(string ConnectionId, TaskLogDeltaEnvelope Envelope);
    private sealed record RecordedTranscriptEvent(string ConnectionId, TranscriptEnvelope Envelope);
    private sealed record RecordedHubEvent(string ConnectionId, string EventName, object? Data);

    private sealed class ListLogger : ILogger<SignalRTaskLogDeltaPublisher>
    {
        private readonly List<string> _entries;
        public ListLogger(List<string> entries) { _entries = entries; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NoopDisposable();
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Add(formatter(state, exception));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
