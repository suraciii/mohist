using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Queries;

public sealed record AgentSessionDto(
    string Id,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string RunnerId,
    string? ExternalSessionId,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? WorkDir,
    string? ChangeDir,
    int? ProcessPid,
    string CreatedAt,
    string? StartedAt,
    string? CompletedAt,
    string? LastDataAt,
    string? FailureReason,
    int? ExitCode);

public sealed record AgentSessionTranscriptEntryDto(string Id, string SessionId, string ProjectId, int IssueNumber, string WorkflowRunId, string WorkId, long Sequence, string Type, object? Payload, string CreatedAt);

public sealed record AgentSessionSummaryDto(
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
    [property: JsonPropertyName("lastDataAt")] string? LastActivityAt,
    string? ProbeSentAt,
    string? ProbeDeadlineAt,
    string? FailureReason);

public sealed record AgentSessionInfoDto(int IssueNumber, string IssueTitle, string IssueStage, string SessionId, [property: JsonPropertyName("status")] string Status, string? Model, string? Title, string CreatedAt, string? CompletedAt, string? LastActivityAt);

public sealed record SessionStartedRequest(string? ExternalSessionId = null, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public sealed record SessionTranscriptEntryRequest(string Type, JsonElement Payload);
public sealed record SessionStatusRequest([property: JsonPropertyName("status")] string Status, DateTime? LastDataAt = null, string? FailureReason = null);
public sealed record SessionCompletedRequest([property: JsonPropertyName("status")] string Status, string? FailureReason = null, int? ExitCode = null);
