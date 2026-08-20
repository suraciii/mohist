using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Infrastructure.Events;

/// <summary>
/// Wire shape for a generic session event carried to the Web over the
/// dedicated non-domain transcript channel (<c>OnTranscriptEvent</c>).
/// Durable history is queried from session transcript turns and parts.
///
/// <para>
/// This is a <i>non-domain</i> envelope: it is runtime event data, not a
/// fact that changes lifecycle state. The <see cref="IEventPublisher"/>
/// bus never sees a <see cref="TranscriptEnvelope"/>, and
/// the domain event push bridge never forwards one.
/// It is also session-scoped: workflow/project/work context belongs to
/// session metadata and workflow references, not to runtime event envelopes.
/// </para>
/// </summary>
public sealed record TranscriptEnvelope(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("runtimeSessionId")] string? RuntimeSessionId,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("createdAt")] string CreatedAt);
