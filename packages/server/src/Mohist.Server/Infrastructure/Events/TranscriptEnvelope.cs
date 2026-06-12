using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Wire shape for a generic session event carried to the Web over the
/// dedicated non-domain transcript channel (<c>OnTranscriptEvent</c>).
/// Durable history is queried from compact transcript segments.
///
/// <para>
/// This is a <i>non-domain</i> envelope: it is observation data, not a
/// fact that changes lifecycle state. The <see cref="IEventPublisher"/>
/// bus never sees a <see cref="TranscriptEnvelope"/>, and
/// <see cref="Mohist.Server.Events.Hub.EventBridge"/> never forwards one.
/// </para>
/// </summary>
public sealed record TranscriptEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("issueNumber")] int IssueNumber,
    [property: JsonPropertyName("workflowRunId")] string WorkflowRunId,
    [property: JsonPropertyName("sessionName")] string SessionName,
    [property: JsonPropertyName("agentSessionId")] string? AgentSessionId,
    [property: JsonPropertyName("workId")] string? WorkId,
    [property: JsonPropertyName("workType")] string? WorkType,
    [property: JsonPropertyName("stage")] string? Stage,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("createdAt")] string CreatedAt);
