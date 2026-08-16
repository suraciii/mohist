using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Api.DirectApi;

/// <summary>
/// The persisted public Session event page. Clients treat nextCursor as
/// opaque and deduplicate returned events by (sessionId, sequence).
/// </summary>
public sealed record PublicEventPage
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<PublicSessionEvent> Events { get; init; }

    [JsonPropertyName("nextCursor")]
    public required string NextCursor { get; init; }

    [JsonPropertyName("highWaterSequence")]
    public required long HighWaterSequence { get; init; }
}

/// <summary>
/// One allowlisted event envelope. Exactly one of Execution and Session
/// is present: execution vocabulary entries carry the full public read;
/// context-reset entries carry the six-key session payload.
/// </summary>
public sealed record PublicSessionEvent
{
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    [JsonPropertyName("cursor")]
    public required string Cursor { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("occurredAt")]
    public required string OccurredAt { get; init; }

    [JsonPropertyName("execution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Execution { get; init; }

    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Session { get; init; }
}
