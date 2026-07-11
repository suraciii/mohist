using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.UnitTests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<RecordedEnvelope> _events = [];
    private readonly Lock _gate = new();

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

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/issues/{issueId}";
            return Task.FromResult<IReadOnlyList<StoredCloudEvent>>(_events
                .Where(e => e.Envelope.Source.ToString() == source)
                .TakeLast(limit)
                .Select((e, idx) => new StoredCloudEvent(idx + 1, e.Envelope))
                .ToList());
        }
    }

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = $"/mohist/epics/{epicId}";
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

    public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);

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
}
