using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Persists CloudEvents 1.0.2 envelopes to per-aggregate tables
/// (<c>WorkflowRunEvents</c> for workflow runs, <c>IssueEvents</c> for
/// issues). Storage shape is the envelope itself, not the raw payload,
/// so reads and writes share the same on-disk structure as the bus
/// dispatch path.
/// </summary>
public interface IEventStore
{
    Task AppendAsync(CloudEvent envelope, CancellationToken ct = default);
    Task<IReadOnlyList<StoredCloudEvent>> ListAsync(string workflowRunId, int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(string issueId, int limit = 200, CancellationToken ct = default);
}

public sealed record StoredCloudEvent(
    long Id,
    CloudEvent Envelope);

/// <summary>
/// Legacy DTO retained for back-compat with the pre-envelope read path
/// (<c>WorkflowEventPersistence.ToDto</c>) and the <see cref="IEventStore"/>
/// impl. New consumers should read <see cref="StoredCloudEvent"/> directly.
/// </summary>
public sealed record WorkflowDomainEventDto(
    long Id,
    string Source,
    string Type,
    WorkflowEvent Data,
    DateTime Time,
    string SpecVersion);
