using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.UnitTests.Support;

public sealed class NoopDeadLetterStore : IDeadLetterStore
{
    public Task WriteAsync(DeadLetterRow row, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DeadLetterRow>> QueryAsync(string? failingHandler, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterRow>>([]);

    public Task<DeadLetterRow?> GetAsync(long deadLetterId, CancellationToken ct = default) =>
        Task.FromResult<DeadLetterRow?>(null);
}