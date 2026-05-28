using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions.Queries;

public sealed record SessionDto(
    string Id,
    string ProjectId,
    int IssueNumber,
    string WorkflowRunId,
    string SessionName,
    string? WorkId,
    string? WorkType,
    string? Stage,
    string? Title,
    string? RunnerId,
    string? AgentSessionId,
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

public sealed record SessionEventDto(string Id, string SessionId, string ProjectId, int IssueNumber, string WorkflowRunId, string SessionName, string? AgentSessionId, string? WorkId, string? WorkType, string? Stage, long Sequence, string Type, object? Payload, string CreatedAt);

public sealed record SessionSummaryDto(
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

public sealed record SessionTranscript(
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
    IReadOnlyList<SessionTranscriptItem> WorkflowLogs);

public sealed record SessionTranscriptItem(string Id, string EventType, object? Data, string CreatedAt);

public sealed record SessionInfoDto(int IssueNumber, string IssueTitle, string IssueStage, string SessionId, [property: JsonPropertyName("status")] string Status, string? Model, string? Title, string CreatedAt, string? CompletedAt, string? LastActivityAt);

public sealed record WorkflowSessionDto(
    string Id,
    string WorkflowRunId,
    string SessionName,
    string? AgentSessionId,
    string? ProjectId,
    int? IssueNumber,
    string? RunnerId,
    string Status,
    string? Model,
    string? WorkDir,
    int? ProcessPid,
    string CreatedAt,
    string? StartedAt,
    string? LastDataAt,
    string? CompletedAt,
    string? FailureReason,
    int? ExitCode);

public sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, IReadOnlyList<SessionEventDto> Events);

public sealed record ActivityDto(
    ActivitySummaryDto Summary,
    IReadOnlyList<ActivityCardDto> Sessions,
    IReadOnlyList<ActivityWaitingCardDto> Waiting);

public sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);

public sealed record ActivitySlotUsageDto(int Active, int Max);

public sealed record ActivityCardDto(
    string IssueId,
    int IssueNumber,
    string IssueTitle,
    string IssueStage,
    string? IssueRuntimeStatus,
    string SessionId,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? Title,
    string CreatedAt,
    string? CompletedAt,
    string LastActivityAt,
    ActivityWorkItemDto? CurrentWorkItem,
    ActivityTaskProgressDto? TaskProgress,
    ActivityPreviewDto? LastActivity,
    string? FailureReason);

public sealed record ActivityWorkItemDto(string Type, string Id, string Title, string? Stage, string? SessionWorkType);
public sealed record ActivityTaskProgressDto(int Completed, int Total);
public sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
public sealed record ActivityWaitingCardDto(string IssueId, int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);

public sealed record SessionStartedRequest(string? ExternalSessionId = null, string? Model = null, string? WorkDir = null, string? ChangeDir = null, int? ProcessPid = null);
public sealed record SessionTranscriptEntryRequest(string Type, JsonElement Payload);
public sealed record SessionStatusRequest([property: JsonPropertyName("status")] string Status, DateTime? LastDataAt = null, string? FailureReason = null);
public sealed record SessionCompletedRequest([property: JsonPropertyName("status")] string Status, string? FailureReason = null, int? ExitCode = null);