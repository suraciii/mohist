using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.UnitTests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<RecordedEnvelope> _events = [];
    private readonly List<UndeliveredEvent> _undelivered = [];
    private readonly List<RecordedDispatch> _marked = [];
    private readonly Lock _gate = new();

    public void SeedUndelivered(params UndeliveredEvent[] events)
    {
        lock (_gate)
        {
            _undelivered.AddRange(events);
        }
    }

    public int ListUndeliveredCallCount { get; private set; }

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(new RecordedEnvelope(envelope));
        }
        return Task.CompletedTask;
    }

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(new RecordedEnvelope(envelope));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/workflow-runs/{workflowRunId}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/projects/{projectId}/issues/{issueNumber}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/projects/{projectId}/epics/{epicNumber}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/agent-session/{sessionId}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/agent-job/{agentJobId}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/projects/{projectId}/workspaces/{name}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _marked.Add(new RecordedDispatch(origin, source, id, dispatchedAt));
            _undelivered.RemoveAll(row => row.Origin == origin && row.Source == source && row.Id == id);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ListUndeliveredCallCount++;
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(_undelivered
                .OrderBy(row => row.Source, StringComparer.Ordinal)
                .ThenBy(row => row.Id)
                .Take(limit)
                .ToList());
        }
    }

    public IReadOnlyList<RecordedDispatch> MarkedDispatched
    {
        get
        {
            lock (_gate)
            {
                return _marked.ToArray();
            }
        }
    }

    public IReadOnlyList<RecordedEnvelope> Appended
    {
        get
        {
            lock (_gate)
            {
                return _events.Select(r => new RecordedEnvelope(r.Envelope)).ToArray();
            }
        }
    }

    public sealed record RecordedEnvelope(CloudEvent Envelope);

    public sealed record RecordedDispatch(
        EventOrigin Origin,
        string Source,
        long Id,
        DateTimeOffset DispatchedAt);
}
