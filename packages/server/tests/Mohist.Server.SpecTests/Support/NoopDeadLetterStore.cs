using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Support;

public sealed class NoopDeadLetterStore : IDeadLetterStore
{
    public Task WriteAsync(DeadLetterRecord record, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<DeadLetterRecord>> ListAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterRecord>>([]);
}
