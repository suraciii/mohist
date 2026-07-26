using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;
using System.Text.Json;

namespace Mohist.Server.SpecTests.Support;

public class RecordingEventStore : IEventStore
{
    private readonly List<RecordedEnvelope> _events = [];
    private readonly HashSet<(string Source, long Id)> _dispatched = [];
    private readonly Lock _gate = new();

    public Func<CloudEvent, bool>? ThrowOnAppend { get; set; }

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        ThrowIfConfigured(envelope);
        lock (_gate)
        {
            _events.Add(new RecordedEnvelope(envelope, NextId(envelope.Source.ToString())));
        }
        return Task.CompletedTask;
    }

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
    {
        ThrowIfConfigured(envelope);
        lock (_gate)
        {
            _events.Add(new RecordedEnvelope(envelope, NextId(envelope.Source.ToString())));
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
                .Select(e => new StoredCloudEvent(e.Id, e.Envelope))
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
                .Select(e => new StoredCloudEvent(e.Id, e.Envelope))
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
                .Select(e => new StoredCloudEvent(e.Id, e.Envelope))
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
                .Select(e => new StoredCloudEvent(e.Id, e.Envelope))
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
                .Select(e => new StoredCloudEvent(e.Id, e.Envelope))
                .ToList());
        }
    }

    public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _dispatched.Add((source, id));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(_events
                .Where(recorded => !_dispatched.Contains((recorded.Envelope.Source.ToString(), recorded.Id)))
                .OrderBy(recorded => recorded.Envelope.Source.ToString(), StringComparer.Ordinal)
                .ThenBy(recorded => recorded.Id)
                .Take(limit)
                .Select(ToUndelivered)
                .ToArray());
        }
    }

    public IReadOnlyList<RecordedEnvelope> Appended
    {
        get
        {
            lock (_gate)
            {
                return _events.Select(r => new RecordedEnvelope(r.Envelope, r.Id)).ToArray();
            }
        }
    }

    private void ThrowIfConfigured(CloudEvent envelope)
    {
        if (ThrowOnAppend?.Invoke(envelope) == true)
            throw new InvalidOperationException("simulated IEventStore append failure");
    }

    private long NextId(string source) =>
        _events.Where(recorded => recorded.Envelope.Source.ToString() == source)
            .Select(recorded => recorded.Id)
            .DefaultIfEmpty()
            .Max() + 1;

    private static UndeliveredEvent ToUndelivered(RecordedEnvelope recorded)
    {
        var envelope = recorded.Envelope;
        return new UndeliveredEvent(
            Origin: OriginFor(envelope.Source.ToString()),
            Id: recorded.Id,
            Source: envelope.Source.ToString(),
            EventId: envelope.Id,
            Type: envelope.Type,
            Time: envelope.Time,
            SpecVersion: envelope.SpecVersion,
            Subject: envelope.Subject,
            DataContentType: envelope.DataContentType ?? "application/json",
            Data: envelope.Data ?? JsonSerializer.SerializeToElement<object?>(null, CloudEvent.JsonOptions),
            ExtensionsJson: JsonSerializer.Serialize(envelope.Extensions, CloudEvent.JsonOptions));
    }

    private static EventOrigin OriginFor(string source)
    {
        if (source.StartsWith("/mohist/issues/", StringComparison.Ordinal))
            return EventOrigin.Issue;
        if (source.StartsWith("/mohist/epics/", StringComparison.Ordinal))
            return EventOrigin.Epic;
        if (source.StartsWith("/mohist/agent-session/", StringComparison.Ordinal))
            return EventOrigin.AgentSession;
        if (source.StartsWith("/mohist/agent-job/", StringComparison.Ordinal))
            return EventOrigin.AgentJob;
        return EventOrigin.WorkflowRun;
    }

    public sealed record RecordedEnvelope(CloudEvent Envelope, long Id = 0);
}
