using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Wire shape for a single incremental task-log batch delivered to the
/// Web over the dedicated non-domain task-log channel
/// (<c>OnTaskLogDelta</c>). Authoritative history remains the
/// <c>TaskLogStore</c>; this envelope carries *only* the delta a
/// runner has just persisted.
///
/// <para>
/// This is a <i>non-domain</i> envelope: it is the work-scoped runtime
/// counterpart to <see cref="TranscriptEnvelope"/>, not a domain event.
/// The <see cref="IEventPublisher"/> bus never sees a
/// <see cref="TaskLogDeltaEnvelope"/>, and
/// the domain event push bridge never forwards
/// one. It is <i>work-scoped</i> (the runner uploads by
/// <c>workId</c>); the server stamps <see cref="TaskId"/> at publish
/// time so the Web, which natively holds <c>taskId</c>, can match
/// incoming deltas against its expanded task without an extra
/// resolve round trip.
/// </para>
///
/// <para>
/// <b>Channel separation</b>: This type is deliberately distinct from
/// <see cref="TranscriptEnvelope"/>. Agent-session transcript deltas
/// flow on <c>OnTranscriptEvent</c> and are session-scoped; ops
/// task-log deltas flow on <c>OnTaskLogDelta</c> and are
/// work/task-scoped. The two channels do not share a subscription
/// dimension or a fan-out publisher.
/// </para>
/// </summary>
public sealed record TaskLogDeltaEnvelope(
    [property: JsonPropertyName("ownerKind")] string OwnerKind,
    [property: JsonPropertyName("ownerId")] string OwnerId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("workId")] string WorkId,
    [property: JsonPropertyName("taskId")] string? TaskId,
    [property: JsonPropertyName("entries")] IReadOnlyList<TaskLogDeltaEntry> Entries,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>
/// Single line inside a <see cref="TaskLogDeltaEnvelope"/>. Shape
/// mirrors the wire entries the runner uploads via
/// <c>POST /api/{workflow-runs|agent-jobs}/{ownerId}/work/{workId}/task-log</c>;
/// the Web reconciles by <c>Seq</c> against its cached log.
/// </summary>
public sealed record TaskLogDeltaEntry(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("text")] string Text);
