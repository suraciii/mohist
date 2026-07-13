using Mohist.Server.Infrastructure.Data.Events;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Persistence seam for dead-lettered events. The dispatcher writes a
/// row when a handler's retries exhaust (and sets the original event
/// row's <c>DispatchedAt</c> so the dispatcher stops retrying it).
/// Operators read rows to inspect poison messages and re-deliver them
/// once the underlying cause is resolved.
/// </summary>
public interface IDeadLetterStore
{
    Task WriteAsync(DeadLetterRow row, CancellationToken ct = default);

    Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(
        string handler,
        int limit = 100,
        CancellationToken ct = default);

    Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100,
        CancellationToken ct = default);

    Task RetryAsync(long deadLetterId, CancellationToken ct = default);

    Task SettleAsync(
        UndeliveredEvent sourceEvent,
        IReadOnlyList<DeadLetterRow> rows,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default);

    Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default);

    Task<DeadLetterRow?> StartRedeliveryAsync(
        long deadLetterId,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default);

    Task RecordRedeliveryFailureAsync(
        long deadLetterId,
        string errorMessage,
        string? errorStack,
        int attemptCount,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default);

    Task ResolveAsync(
        long deadLetterId,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default);

    Task DeleteAsync(long deadLetterId, CancellationToken ct = default);
}