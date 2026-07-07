using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.SpecTests.Support;

public class NoopEventStore : IEventStore
{
    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);

    public Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string epicId, int limit = 200, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoredCloudEvent>>([]);
}
