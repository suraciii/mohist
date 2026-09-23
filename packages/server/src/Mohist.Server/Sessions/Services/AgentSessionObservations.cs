using System.Text.Json.Serialization;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public sealed record AgentSessionInputObservationDto(
    string Id,
    long Sequence,
    string Source,
    string Acceptance,
    IReadOnlyList<AgentSessionInputAttachmentObservationDto>? Attachments = null,
    [property: JsonPropertyName("provenance")] AgentSessionInputProvenance? Provenance = null,
    [property: JsonPropertyName("startupContext")] AgentStartupContextObservationDto? StartupContext = null,
    [property: JsonPropertyName("contextGeneration")] long ContextGeneration = 1);

public sealed record AgentStartupContextObservationDto(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("truncationMarker")] string? TruncationMarker,
    [property: JsonPropertyName("omittedOldestMessageCount")] int OmittedOldestMessageCount);

public sealed record AgentSessionInputAttachmentObservationDto(
    string Id,
    string Name,
    string? ContentType,
    long Size,
    string Source,
    string Availability);

public sealed record AgentTurnObservationDto(
    string Id,
    long Sequence,
    IReadOnlyList<string> InputIds,
    string Status,
    [property: JsonPropertyName("result")] AgentTurnResultObservationDto? Result = null,
    [property: JsonPropertyName("contextGeneration")] long ContextGeneration = 1,
    [property: JsonPropertyName("supersededAt")] string? SupersededAt = null);

public sealed record AgentTurnResultObservationDto(
    string? Message,
    string? Output,
    string? FailureReason,
    string? FailureCategory,
    int? ExitCode);

public sealed record AgentSessionRecoveryObservationDto(
    string Type,
    string RecordedAt,
    string? RuntimeSessionId,
    string? Reason,
    string? Strategy,
    string? Summary,
    long? ContextWindowUsedBefore,
    long? ContextWindowUsedAfter,
    long? ContextWindowSize);
