using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mohist.Server.Events.Grains;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.TestSupport;
using Orleans;
using Xunit;

namespace Mohist.Server.TestSupport;

internal sealed class AgentSessionTransactionalThrowingEventStore : IEventStore
{
    private int _callCount;

    public Task AppendAsync(CloudEvent envelope, CancellationToken ct = default) => Task.CompletedTask;

    public async Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default)
    {
        _callCount++;
        if (_callCount >= 2)
        {
            throw new InvalidOperationException("simulated event write failed");
        }
        await db.AgentSessionEvents.AddAsync(new AgentSessionEventRow
        {
            Id = _callCount,
            Source = envelope.Source.ToString(),
            EventId = envelope.Id,
            Type = envelope.Type,
            Time = envelope.Time,
            SpecVersion = envelope.SpecVersion,
            Subject = envelope.Subject,
            DataContentType = envelope.DataContentType ?? "application/json",
            Data = envelope.Data ?? System.Text.Json.JsonDocument.Parse("null").RootElement,
            ExtensionsJson = "{}",
        }, ct);
    }

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
