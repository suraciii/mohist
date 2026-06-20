using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Workflow.Services.Sessions;

public sealed record AgentUsageDto(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CachedReadTokens,
    long? ThoughtTokens,
    double? CostAmount,
    string? CostCurrency,
    long? ContextWindowUsed,
    long? ContextWindowSize,
    double? ContextUsagePercent,
    string? HealthStatus);

public sealed record AgentEventSummaryDto(
    string? ResolvedModel,
    string? FailureCategory,
    bool? ContextExhaustion,
    bool? ContextExhaustionSuspected,
    int? ToolCallCount,
    int? ToolErrorCount);

public sealed record AgentSessionDto(
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
    int? ExitCode,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record AgentSessionMetadataDto(
    string Id,
    string SessionName,
    [property: JsonPropertyName("acpSessionId")] string AgentRuntimeSessionId,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? Stage,
    string? Title,
    string CreatedAt,
    string? CompletedAt,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage,
    [property: JsonPropertyName("metadata")] AgentSessionMetadataCounts Metadata);

public sealed record AgentSessionMetadataCounts(
    [property: JsonPropertyName("partCount")] int PartCount,
    [property: JsonPropertyName("toolCount")] int ToolCount);

public sealed class AgentSessionTranscriptResponse
{
    public IReadOnlyList<AgentSessionTranscriptTurnDto> Turns { get; init; } = [];
    public int PartCount { get; init; }
    public string? LastActivityAt { get; init; }
}

public sealed class AgentSessionTranscriptTurnDto
{
    public string Id { get; init; } = string.Empty;
    public string StartedAt { get; init; } = string.Empty;
    public string? CompletedAt { get; set; }
    public bool Incomplete { get; set; }
    public AgentSessionTranscriptUserDto User { get; init; } = new();
    public List<AgentSessionTranscriptPartDto> Assistant { get; init; } = [];
}

public sealed class AgentSessionTranscriptUserDto
{
    public string Role { get; init; } = "mohist";
    public string Text { get; init; } = string.Empty;
    public string Kind { get; init; } = "task";
    public string SentAt { get; init; } = string.Empty;
}

public sealed class AgentSessionTranscriptPartDto
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? Text { get; set; }
    public AgentSessionTranscriptToolDto? Tool { get; set; }
    public string? Message { get; init; }
    public string? Kind { get; init; }
    public string? StartedAt { get; init; }
    public string? CompletedAt { get; set; }
    public string? At { get; init; }
}

public sealed class AgentSessionTranscriptToolDto
{
    public string ToolCallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = "unknown";
    public string? NormalizedName { get; init; }
    public string Status { get; set; } = "pending";
    public string? Title { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string StartedAt { get; init; } = string.Empty;
    public string? CompletedAt { get; set; }
    public string? RawInput { get; set; }
    public string? RawOutput { get; set; }
}

public sealed record AgentSessionSummaryDto(
    string Id,
    string SessionName,
    [property: JsonPropertyName("acpSessionId")] string AgentRuntimeSessionId,
    [property: JsonPropertyName("executionId")] string? WorkId,
    [property: JsonPropertyName("taskDescription")] string? TaskTitle,
    [property: JsonPropertyName("status")] string Status,
    string CreatedAt,
    string? CompletedAt,
    string? Model,
    [property: JsonPropertyName("coderType")] string? AgentRuntime,
    string? Stage,
    string? Title,
    [property: JsonPropertyName("lastDataAt")] string? LastActivityAt,
    string? ProbeSentAt,
    string? ProbeDeadlineAt,
    string? FailureReason,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record AgentSessionInfoDto(
    int IssueNumber,
    string IssueTitle,
    string IssueStage,
    string SessionId,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? Title,
    string CreatedAt,
    string? CompletedAt,
    string? LastActivityAt,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record WorkflowSessionDto(
    string Id,
    string WorkflowRunId,
    string SessionName,
    [property: JsonPropertyName("acpSessionId")] string? AgentSessionId,
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
    int? ExitCode,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, AgentSessionTranscriptResponse Transcript);

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
    string? FailureReason,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record ActivityWorkItemDto(string Type, string Id, string Title, string? Stage, string? SessionWorkType);
public sealed record ActivityTaskProgressDto(int Completed, int Total);
public sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
public sealed record ActivityWaitingCardDto(string IssueId, int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);

public sealed record AgentSessionStatusRequest([property: JsonPropertyName("status")] string Status, DateTime? LastDataAt = null, string? FailureReason = null);

public sealed record AgentUsageTimeseriesDto(
    DateTime RangeFrom,
    DateTime RangeTo,
    string BucketGranularity,
    IReadOnlyList<UsageBucketDto> Buckets);

public sealed record UsageBucketDto(
    DateTime BucketStart,
    DateTime BucketEnd,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double CostAmount,
    string? CostCurrency);
