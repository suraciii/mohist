using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Persists CloudEvents 1.0.2 envelopes to per-aggregate tables
/// (<c>WorkflowRunEvents</c> for workflow runs, <c>IssueEvents</c> for
/// issues, <c>EpicEvents</c> for epics, <c>AgentSessionEvents</c> for
/// agent sessions). Storage shape is the envelope itself, not the raw
/// payload, so reads and writes share the same on-disk structure as the
/// bus dispatch path.
/// </summary>
public interface IEventStore
{
    Task AppendAsync(CloudEvent envelope, CancellationToken ct = default);

    /// <summary>
    /// Stages a single event row on the caller-supplied <see cref="MohistDbContext"/>
    /// without committing or opening its own transaction. Producers
    /// that already own a state transaction call this overload so the
    /// event row commits atomically with their state write. The caller
    /// owns <c>SaveChangesAsync</c> and the surrounding
    /// <c>BeginTransactionAsync</c> / <c>CommitAsync</c>.
    /// </summary>
    Task AppendAsync(MohistDbContext db, CloudEvent envelope, CancellationToken ct = default);

    Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string projectId, int issueNumber, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<StoredCloudEvent>> ListEpicEventsAsync(string projectId, int epicNumber, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<StoredCloudEvent>> ListAgentSessionEventsAsync(string sessionId, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Marks the row in the truth table identified by the origin returned from
    /// <see cref="ListUndeliveredAsync"/>. Source is the stream identity, not a
    /// persistence-table discriminator.
    /// </summary>
    Task MarkDispatchedAsync(
        EventOrigin origin,
        string source,
        long id,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default);
    Task<IReadOnlyList<UndeliveredEvent>> ListUndeliveredAsync(int limit = 100, CancellationToken ct = default);
}

public sealed record StoredCloudEvent(
    long Id,
    CloudEvent Envelope);

public enum EventOrigin
{
    WorkflowRun,
    Issue,
    Epic,
    AgentSession,
}

public sealed record UndeliveredEvent(
    EventOrigin Origin,
    long Id,
    string Source,
    string EventId,
    string Type,
    DateTimeOffset Time,
    string SpecVersion,
    string? Subject,
    string DataContentType,
    JsonElement Data,
    string ExtensionsJson);

/// <summary>
/// Legacy DTO retained for back-compat with the pre-envelope read path
/// and the <see cref="IEventStore"/> impl. New consumers should read
/// <see cref="StoredCloudEvent"/> directly.
/// </summary>
public sealed record WorkflowDomainEventDto(
    long Id,
    string Source,
    string Type,
    WorkflowEvent Data,
    DateTime Time,
    string SpecVersion);
