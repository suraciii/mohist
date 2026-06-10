using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Tests.Support;

public class NoopEventStore : IEventStore
{
    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
}
