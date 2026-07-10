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

    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default)
    {
        if (ThrowOnWrite?.Invoke(row) == true)
            throw new InvalidOperationException("simulated dead-letter write failure");
        lock (_gate)
        {
            var assignedId = row.DeadLetterId == 0 ? ++_nextId : row.DeadLetterId;
            var stored = new DeadLetterRow
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
            };
            _rows.Add(stored);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IEnumerable<DeadLetterRow> q = _rows;
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

    public IReadOnlyList<DeadLetterRow> Written
    {
        get
        {
            lock (_gate) { return _rows.ToList(); }
        }
    }
}