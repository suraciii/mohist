using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.TestSupport;

public sealed class NoopDeadLetterStore : IDeadLetterStore
{
    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DeadLetterRow>> ListByHandlerAsync(
        string handler,
        int limit = 100,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterRow>>([]);

    public Task<IReadOnlyList<DeadLetterRow>> ListByTimeRangeAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 100,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterRow>>([]);

    public Task RetryAsync(long deadLetterId, CancellationToken ct = default) => Task.CompletedTask;

    public Task SettleAsync(UndeliveredEvent sourceEvent, IReadOnlyList<DeadLetterRow> rows, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterRow>>([]);

    public Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default) =>
        Task.FromResult<DeadLetterRow?>(null);

    public Task<DeadLetterRow?> StartRedeliveryAsync(long deadLetterId, DateTimeOffset attemptedAt, CancellationToken ct = default) =>
        Task.FromResult<DeadLetterRow?>(null);

    public Task RecordRedeliveryFailureAsync(long deadLetterId, string errorMessage, string? errorStack, int attemptCount, DateTimeOffset attemptedAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResolveAsync(long deadLetterId, DateTimeOffset resolvedAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAsync(long deadLetterId, CancellationToken ct = default) => Task.CompletedTask;
}