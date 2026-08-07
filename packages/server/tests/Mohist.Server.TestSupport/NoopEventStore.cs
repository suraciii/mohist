using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.TestSupport;

public class NoopEventStore : IEventStore
{
    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

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

    public Task MarkDispatchedAsync(EventOrigin origin, string source, long id, DateTimeOffset dispatchedAt, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UndeliveredEvent>>([]);
}
