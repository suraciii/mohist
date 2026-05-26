namespace Mohist.Server.Sessions.Queries;

using System.Text.Json.Serialization;

public sealed record AgentSessionTranscript(
    string Id,
    [property: JsonPropertyName("acpSessionId")] string AgentRuntimeSessionId,
    [property: JsonPropertyName("executionId")] string? WorkId,
    [property: JsonPropertyName("taskDescription")] string? TaskTitle,
    [property: JsonPropertyName("status")] string Status,
    string CreatedAt,
    string? CompletedAt,
    string? Model,
    [property: JsonPropertyName("coderType")] string? AgentKind,
    string? Stage,
    string? Title,
    object Metadata,
    object Turns,
    bool Incomplete,
    IReadOnlyList<WorkflowLogItemDto> WorkflowLogs);

public sealed record WorkflowLogItemDto(string Id, string EventType, object? Data, string CreatedAt);
