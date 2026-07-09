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

    Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default);

    Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default);
}