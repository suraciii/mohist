using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.UnitTests.Support;

/// <summary>
/// Recording <see cref="IDeadLetterStore"/> fake for dispatcher unit tests.
/// Captures every <see cref="WriteAsync"/> call and serves them back through
/// <see cref="Written"/>. <see cref="QueryAsync"/> and <see cref="GetAsync"/>
/// delegate to the in-memory list so tests can also assert on the operator
/// query path through <see cref="EventDispatcherService.RedeliverAsync"/>.
/// </summary>
public sealed class FakeDeadLetterStore : IDeadLetterStore
{
    private readonly object _gate = new();
    private readonly List<DeadLetterRow> _rows = [];
    private long _nextId;

    public Func<DeadLetterRow, bool>? ThrowOnWrite { get; set; }
    public bool ThrowOnResolve { get; set; }
    public FakeEventStore? EventStore { get; set; }

    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default)
    {
        if (ThrowOnWrite?.Invoke(row) == true)
            throw new InvalidOperationException("simulated dead-letter write failure");
        lock (_gate)
        {
            var assignedId = row.DeadLetterId == 0 ? ++_nextId : row.DeadLetterId;
            var stored = Clone(row, assignedId);
            _rows.Add(stored);
        }
        return Task.CompletedTask;
    }

    public async Task SettleAsync(
        UndeliveredEvent sourceEvent,
        IReadOnlyList<DeadLetterRow> rows,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default)
    {
        if (rows.Any(row => ThrowOnWrite?.Invoke(row) == true))
            throw new InvalidOperationException("simulated dead-letter settlement failure");

        if (EventStore is not null)
            await EventStore.MarkDispatchedAsync(sourceEvent.Source, sourceEvent.Id, dispatchedAt, ct);

        lock (_gate)
        {
            foreach (var row in rows)
            {
                var existing = _rows.FirstOrDefault(stored =>
                    stored.Source == row.Source
                    && stored.Id == row.Id
                    && stored.FailingHandler == row.FailingHandler);
                if (existing is null)
                {
                    _rows.Add(Clone(row, ++_nextId));
                    continue;
                }

                existing.ErrorMessage = row.ErrorMessage;
                existing.ErrorStack = row.ErrorStack;
                existing.AttemptCount = row.AttemptCount;
                existing.DeadLetteredAt = row.DeadLetteredAt;
                existing.Status = DeadLetterStatus.Pending;
                existing.RedeliveryAttemptedAt = null;
                existing.ResolvedAt = null;
            }
        }
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

    public Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.FirstOrDefault(r => r.DeadLetterId == deadLetterId));
        }
    }

    public Task<DeadLetterRow?> StartRedeliveryAsync(
        long deadLetterId,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default)
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

    public Task RecordRedeliveryFailureAsync(
        long deadLetterId,
        string errorMessage,
        string? errorStack,
        int attemptCount,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default)
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
        if (ThrowOnResolve)
            throw new InvalidOperationException("simulated dead-letter resolve failure");
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
        lock (_gate)
        {
            _rows.RemoveAll(row => row.DeadLetterId == deadLetterId);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<DeadLetterRow> Written
    {
        get
        {
            lock (_gate) { return _rows.Where(row => row.Status != DeadLetterStatus.Resolved).ToList(); }
        }
    }

    private static DeadLetterRow Clone(DeadLetterRow row, long deadLetterId) =>
        new()
        {
            DeadLetterId = deadLetterId,
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
