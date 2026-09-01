using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.TestSupport;
using Xunit;

namespace Mohist.Server.TestSupport;

/// <summary>
/// Captures every CloudEvent <see cref="IEventStore.AppendAsync"/> call and
/// serves the same events back as fresh undelivered rows. Lets spec tests
/// drive the dispatcher's claim–drain–settle cycle against the real
/// <see cref="DispatchStreamLeaseStore"/> on SQLite, with the rest of the
/// store seams faked.
/// </summary>
public sealed class CapturingEventStore : IEventStore
{
    private readonly List<UndeliveredEvent> _rows = [];
    private long _nextId;
    private readonly object _gate = new();

    internal Action<UndeliveredEvent>? SettlementObserver { get; set; }

    public Func<CloudEvent, bool>? ThrowOnAppend { get; set; }

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default)
    {
        if (ThrowOnAppend?.Invoke(envelope) == true)
            throw new InvalidOperationException("simulated event append failure");
        lock (_gate)
        {
            _rows.Add(new UndeliveredEvent(
                Origin: ResolveOrigin(envelope.Source.ToString()),
                Id: ++_nextId,
                Source: envelope.Source.ToString(),
                EventId: envelope.Id,
                Type: envelope.Type,
                Time: envelope.Time,
                SpecVersion: envelope.SpecVersion,
                Subject: envelope.Subject,
                DataContentType: envelope.DataContentType ?? "application/json",
                Data: envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
                ExtensionsJson: envelope.Extensions.Count == 0
                    ? "{}"
                    : System.Text.Json.JsonSerializer.Serialize(envelope.Extensions, CloudEvent.JsonOptions)));
        }
        return Task.CompletedTask;
    }

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default) =>
        AppendAsync(envelope, ct);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListAgentJobEventsAsync(string agentJobId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListWorkspaceEventsAsync(string projectId, string name, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task MarkDispatchedAsync(
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var settled = RemoveUndelivered(origin, source, id);
        NotifySettlement(settled);
        return Task.CompletedTask;
    }

    internal UndeliveredEvent RemoveUndelivered(EventOrigin origin, string source, long id)
    {
        lock (_gate)
        {
            var matches = _rows
                .Where(r => r.Origin == origin && r.Source == source && r.Id == id)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one event settlement for {origin}/{source}/{id}, found {matches.Length}.");
            var settled = matches[0];
            _rows.Remove(settled);
            return settled;
        }
    }

    internal void NotifySettlement(UndeliveredEvent settled) => SettlementObserver?.Invoke(settled);

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(_rows
                .OrderBy(r => r.Source, StringComparer.Ordinal)
                .ThenBy(r => r.Id)
                .Take(limit)
                .Select(r => r)
                .ToList());
        }
    }

    public Task<IReadOnlyList<PendingStream>> ListPendingStreamsAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PendingStream>>(_rows
                .GroupBy(r => (r.Origin, r.Source))
                .Select(group => new PendingStream(
                    group.Key.Origin,
                    group.Key.Source,
                    group.Min(r => r.Time)))
                .OrderBy(stream => stream.OldestPendingTime)
                .Take(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredByStreamAsync(
        EventOrigin origin,
        string source,
        int limit,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<UndeliveredEvent>>(_rows
                .Where(r => r.Origin == origin && r.Source == source)
                .OrderBy(r => r.Id)
                .Take(limit)
                .ToList());
        }
    }

    public Task MarkDispatchedRangeAsync(
        EventOrigin origin,
        string source,
        IReadOnlyList<long> ids,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<UndeliveredEvent> settled = [];
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var match = _rows.SingleOrDefault(r =>
                    r.Origin == origin && r.Source == source && r.Id == id);
                if (match is not null)
                {
                    _rows.Remove(match);
                    settled.Add(match);
                }
            }
        }
        foreach (var row in settled)
            NotifySettlement(row);
        return Task.CompletedTask;
    }

    public int PendingCount
    {
        get { lock (_gate) { return _rows.Count; } }
    }

    internal void AddUndeliveredShadow(UndeliveredEvent row)
    {
        lock (_gate)
            _rows.Add(row);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _nextId = 0;
            ThrowOnAppend = null;
        }
    }

    /// <summary>
    /// Re-queues a row that was previously marked dispatched so the next
    /// <see cref="ListUndeliveredAsync"/> returns it. Mirrors the production
    /// path where <c>DeadLetterStore.RetryAsync</c> re-nulls the source
    /// event's <c>DispatchedAt</c>.
    /// </summary>
    public void ReQueueForRedelivery(string origin, string source, long id)
    {
        var originEnum = ParseOriginName(origin);
        lock (_gate)
        {
            var existing = _rows.FirstOrDefault(e =>
                e.Origin == originEnum && e.Source == source && e.Id == id);
            if (existing is null)
            {
                _rows.Add(new UndeliveredEvent(
                    Origin: originEnum,
                    Id: id,
                    Source: source,
                    EventId: $"evt-retry-{id}",
                    Type: "com.mohist.retry",
                    Time: DateTimeOffset.UnixEpoch,
                    SpecVersion: "1.0",
                    Subject: null,
                    DataContentType: "application/json",
                    Data: System.Text.Json.JsonDocument.Parse("null").RootElement,
                    ExtensionsJson: "{}"));
            }
        }
    }

    internal StateSnapshot CaptureState()
    {
        lock (_gate)
        {
            return new StateSnapshot(_rows.ToList(), _nextId);
        }
    }

    internal void RestoreState(StateSnapshot snapshot)
    {
        lock (_gate)
        {
            _rows.Clear();
            _rows.AddRange(snapshot.Rows);
            _nextId = snapshot.NextId;
        }
    }

    internal sealed record StateSnapshot(IReadOnlyList<UndeliveredEvent> Rows, long NextId);

    private static EventOrigin ResolveOrigin(string source)
    {
        if (source.StartsWith("/mohist/workflow-runs/", StringComparison.Ordinal)) return EventOrigin.WorkflowRun;
        if (source.StartsWith("/mohist/issues/", StringComparison.Ordinal)) return EventOrigin.Issue;
        if (source.StartsWith("/mohist/epics/", StringComparison.Ordinal)) return EventOrigin.Epic;
        if (source.StartsWith("/mohist/agent-session/", StringComparison.Ordinal)) return EventOrigin.AgentSession;
        if (source.StartsWith("/mohist/agent-job/", StringComparison.Ordinal)) return EventOrigin.AgentJob;
        if (source.StartsWith("/mohist/projects/", StringComparison.Ordinal))
        {
            if (source.Contains("/issues/", StringComparison.Ordinal)) return EventOrigin.Issue;
            if (source.Contains("/epics/", StringComparison.Ordinal)) return EventOrigin.Epic;
            if (source.Contains("/workspaces/", StringComparison.Ordinal)) return EventOrigin.Workspace;
            if (source.Contains("/github-connections/", StringComparison.Ordinal)) return EventOrigin.Ingress;
        }
        if (source == "/mohist/inbox"
            || source.StartsWith("/mohist/inbox/", StringComparison.Ordinal))
            return EventOrigin.WorkflowRun;
        throw new InvalidOperationException($"Unknown event source '{source}'.");
    }

    private static EventOrigin ParseOriginName(string origin) => origin switch
    {
        nameof(EventOrigin.WorkflowRun) => EventOrigin.WorkflowRun,
        nameof(EventOrigin.Issue) => EventOrigin.Issue,
        nameof(EventOrigin.Epic) => EventOrigin.Epic,
        nameof(EventOrigin.AgentSession) => EventOrigin.AgentSession,
        nameof(EventOrigin.AgentJob) => EventOrigin.AgentJob,
        nameof(EventOrigin.Ingress) => EventOrigin.Ingress,
        nameof(EventOrigin.Workspace) => EventOrigin.Workspace,
        _ => throw new InvalidOperationException($"Unknown event origin '{origin}'."),
    };
}

/// <summary>
/// In-memory <see cref="IDeadLetterStore"/> for the dispatcher fixture.
/// Records every dead-letter write and supports the query/get paths so
/// spec tests can assert the engine → dead-letter wiring.
/// </summary>
public sealed class CapturingDeadLetterStore : IDeadLetterStore
{
    private readonly object _gate = new();
    private readonly List<DeadLetterRow> _rows = [];
    private readonly CapturingEventStore _events;
    private long _nextId;

    public bool ThrowAfterSourceMark { get; set; }

    public CapturingDeadLetterStore(CapturingEventStore events)
    {
        _events = events;
    }

    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var assignedId = row.DeadLetterId == 0 ? ++_nextId : row.DeadLetterId;
            _rows.Add(new DeadLetterRow
            {
                DeadLetterId = assignedId,
                Origin = row.Origin,
                Id = row.Id,
                Source = row.Source,
                EventId = row.EventId,
                Type = row.Type,
                Time = row.Time,
                SpecVersion = row.SpecVersion,
                Subject = row.Subject,
                DataContentType = row.DataContentType,
                Data = row.Data,
                ExtensionsJson = row.ExtensionsJson,
                FailingHandler = row.FailingHandler,
                ErrorMessage = row.ErrorMessage,
                ErrorStack = row.ErrorStack,
                AttemptCount = row.AttemptCount,
                DeadLetteredAt = row.DeadLetteredAt,
            });
        }
        return Task.CompletedTask;
    }

    public async Task SettleAsync(
        UndeliveredEvent sourceEvent,
        IReadOnlyList<DeadLetterRow> rows,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var eventSnapshot = _events.CaptureState();
        List<DeadLetterRow> rowSnapshot;
        long nextIdSnapshot;
        UndeliveredEvent settled;
        lock (_gate)
        {
            rowSnapshot = _rows.Select(Clone).ToList();
            nextIdSnapshot = _nextId;
        }

        try
        {
            settled = _events.RemoveUndelivered(
                sourceEvent.Origin,
                sourceEvent.Source,
                sourceEvent.Id);
            if (ThrowAfterSourceMark)
                throw new InvalidOperationException("simulated post-mark settlement failure");
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await WriteAsync(row, ct);
            }
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            _events.RestoreState(eventSnapshot);
            lock (_gate)
            {
                _rows.Clear();
                _rows.AddRange(rowSnapshot);
                _nextId = nextIdSnapshot;
            }
            throw;
        }

        _events.NotifySettlement(settled);
    }

    public Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<DeadLetterRow> q = _rows.Where(row => row.Status != DeadLetterStatus.Resolved);
            if (!string.IsNullOrEmpty(failingHandler))
                q = q.Where(r => r.FailingHandler == failingHandler);
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(q
                .OrderBy(r => r.DeadLetteredAt)
                .ThenBy(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(
        string handler,
        int limit = 100,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(_rows
                .Where(r => r.FailingHandler == handler)
                .OrderByDescending(r => r.DeadLetteredAt)
                .ThenByDescending(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeadLetterRow>>(_rows
                .Where(r => r.DeadLetteredAt >= from && r.DeadLetteredAt < to)
                .OrderBy(r => r.DeadLetteredAt)
                .ThenBy(r => r.DeadLetterId)
                .Take(limit)
                .ToList());
        }
    }

    public Task RetryAsync(long deadLetterId, CancellationToken ct = default)
    {
        DeadLetterRow? row;
        lock (_gate)
        {
            row = _rows.FirstOrDefault(r => r.DeadLetterId == deadLetterId);
        }
        if (row is null)
            throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");
        _events.ReQueueForRedelivery(row.Origin, row.Source, row.Id);
        return Task.CompletedTask;
    }

    public Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.FirstOrDefault(r => r.DeadLetterId == deadLetterId));
        }
    }

    public Task<DeadLetterRow?> StartRedeliveryAsync(long deadLetterId, DateTimeOffset attemptedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.FirstOrDefault(row => row.DeadLetterId == deadLetterId);
            if (row is null || row.Status == DeadLetterStatus.Resolved)
                return Task.FromResult<DeadLetterRow?>(null);
            row.Status = DeadLetterStatus.Redelivering;
            row.RedeliveryAttemptedAt = attemptedAt;
            return Task.FromResult<DeadLetterRow?>(row);
        }
    }

    public Task RecordRedeliveryFailureAsync(long deadLetterId, string errorMessage, string? errorStack, int attemptCount, DateTimeOffset attemptedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.Single(row => row.DeadLetterId == deadLetterId);
            row.Status = DeadLetterStatus.Pending;
            row.ErrorMessage = errorMessage;
            row.ErrorStack = errorStack;
            row.AttemptCount = attemptCount;
            row.RedeliveryAttemptedAt = attemptedAt;
        }
        return Task.CompletedTask;
    }

    public Task ResolveAsync(long deadLetterId, DateTimeOffset resolvedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.Single(row => row.DeadLetterId == deadLetterId);
            row.Status = DeadLetterStatus.Resolved;
            row.ResolvedAt = resolvedAt;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(long deadLetterId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var removed = _rows.RemoveAll(row => row.DeadLetterId == deadLetterId);
            if (removed != 1)
                throw new InvalidOperationException($"Dead-letter row '{deadLetterId}' was not found.");
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<DeadLetterRow> Written
    {
        get { lock (_gate) { return _rows.Where(row => row.Status != DeadLetterStatus.Resolved).ToList(); } }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rows.Clear();
            _nextId = 0;
            ThrowAfterSourceMark = false;
        }
    }

    private static DeadLetterRow Clone(DeadLetterRow row) =>
        new()
        {
            DeadLetterId = row.DeadLetterId,
            Origin = row.Origin,
            Id = row.Id,
            Source = row.Source,
            EventId = row.EventId,
            Type = row.Type,
            Time = row.Time,
            SpecVersion = row.SpecVersion,
            Subject = row.Subject,
            DataContentType = row.DataContentType,
            Data = row.Data,
            ExtensionsJson = row.ExtensionsJson,
            FailingHandler = row.FailingHandler,
            ErrorMessage = row.ErrorMessage,
            ErrorStack = row.ErrorStack,
            AttemptCount = row.AttemptCount,
            DeadLetteredAt = row.DeadLetteredAt,
            Status = row.Status,
            RedeliveryAttemptedAt = row.RedeliveryAttemptedAt,
            ResolvedAt = row.ResolvedAt,
        };
}

