using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mohist.Server.Sessions;

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
    string? HealthStatus,
    [property: JsonPropertyName("contextUsageHistory")] IReadOnlyList<ContextUsageHistoryEntryDto>? ContextUsageHistory = null);

/// <summary>
/// DTO projection of <see cref="Mohist.Server.Sessions.Domain.ContextUsageHistoryEntry"/>.
/// One sample of the bounded context-usage history exposed on
/// <see cref="AgentUsageDto.ContextUsageHistory"/> so the Pulse
/// zone can render a context-usage trend mini-chart from the live
/// activity feed (issue-245 T-002 / design D5). The list is omitted
/// from the wire when empty (<c>JsonIgnoreCondition.WhenWritingNull</c>).
/// </summary>
public sealed record ContextUsageHistoryEntryDto(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("percent")] double Percent);

public sealed record AgentEventSummaryDto(
    string? ResolvedModel,
    string? FailureCategory,
    bool? ContextExhaustion,
    bool? ContextExhaustionSuspected,
    int? ToolCallCount,
    int? ToolErrorCount);

public sealed record AgentSessionMetadataDto(
    string Id,
    string SessionName,
    [property: JsonPropertyName("runtimeSessionId")] string? AgentRuntimeSessionId,
    [property: JsonPropertyName("runtime")] string? AgentRuntime,
    [property: JsonPropertyName("status")] string Status,
    string? Model,
    string? Stage,
    string? Title,
    string CreatedAt,
    string? CompletedAt,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage,
    [property: JsonPropertyName("metadata")] AgentSessionMetadataCounts Metadata,
    [property: JsonPropertyName("runtimeSessionLineage")] IReadOnlyList<RuntimeSessionLineageEntryDto>? RuntimeSessionLineage);

/// <summary>
/// DTO projection of <see cref="Mohist.Server.Sessions.Domain.RuntimeSessionLineageEntry"/>.
/// Ordered by binding time. The first entry is the original runtime
/// session; each subsequent entry records a compact/reset rebind
/// successor. Absent on the wire when the chain is empty (historical
/// sessions compacted before T-001) so the field degrades to hidden.
/// </summary>
public sealed record RuntimeSessionLineageEntryDto(
    [property: JsonPropertyName("runtimeSessionId")] string? AgentRuntimeSessionId,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("boundAt")] string BoundAt);

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
    public string? RuntimeSessionId { get; init; }
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
    [property: JsonPropertyName("runtimeSessionId")] string? AgentRuntimeSessionId,
    [property: JsonPropertyName("executionId")] string? WorkId,
    [property: JsonPropertyName("taskDescription")] string? TaskTitle,
    [property: JsonPropertyName("status")] string Status,
    string CreatedAt,
    string? CompletedAt,
    string? Model,
    [property: JsonPropertyName("runtime")] string? AgentRuntime,
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
    [property: JsonPropertyName("runtimeSessionId")] string? AgentSessionId,
    [property: JsonPropertyName("runtime")] string? Runtime,
    string? ProjectId,
    int? IssueNumber,
    string? RunnerId,
    string Status,
    string? Stage,
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

/// <summary>
/// Read shape for a generic (non-workflow) <see cref="Sessions.Domain.AgentSession"/>
/// as surfaced by the agent-scoped list endpoint
/// (<c>GET /api/projects/{projectRef}/agents/{agentRef}/sessions</c>).
/// Issued-130 T-002 / design D2: the status field carries one of the
/// spec vocabulary values (<c>running</c> / <c>completed</c> / <c>failed</c>
/// / <c>stopped</c>) so the workbench can derive the four primary state
/// groupings (recent / running / failed / ended) directly from the list
/// output, and the legacy runner-protocol <c>cancelled</c> alias is
/// normalised to <c>stopped</c>. <see cref="ContextRefs"/> is the
/// optional envelope of context references stamped on the session at
/// launch (issue / epic / repository / workspace path); absent when the
/// session carried no such reference. Workflow-shaped fields
/// (<c>workflowRunId</c>, <c>sessionName</c>, <c>workId</c>,
/// <c>workType</c>, <c>stage</c>) are omitted by construction.
/// </summary>
public sealed record AgentSessionListItemDto(
    string SessionId,
    string AgentId,
    string AgentName,
    [property: JsonPropertyName("status")] string Status,
    string CreatedAt,
    string? LastActivityAt,
    string? ResolvedModel,
    [property: JsonPropertyName("contextRefs")] AgentSessionListContextRefsDto? ContextRefs);

/// <summary>
/// Optional envelope of context references recorded on a generic
/// AgentSession at launch (issue-130 T-002). Each field is null when the
/// session carried no such reference; the envelope itself is null when
/// the session had no context references at all.
/// </summary>
public sealed record AgentSessionListContextRefsDto(
    int? IssueNumber,
    int? EpicNumber,
    string? Repository,
    string? WorkspacePath);

/// <summary>
/// Read shape for the generic-session summary route
/// (<c>GET /api/projects/{projectRef}/agent-sessions/{sessionId}</c>),
/// surfaced as the
/// <see cref="Sessions.Services.AgentSessionQuerier.GetGenericSessionSummaryAsync"/>
/// response. Issue-130 T-003 / design D4: the summary carries the
/// resolved Agent profile identity (id + name), the session status in
/// the spec vocabulary (<c>running</c> / <c>completed</c> / <c>failed</c>
/// / <c>stopped</c>), the created / last-activity timestamps, the
/// resolved model, the usage metrics, the failure category (when
/// present), the tool call and tool error counts, and the optional
/// context references stamped at launch. Workflow-only fields
/// (<c>workflowRunId</c>, <c>sessionName</c>, <c>workId</c>,
/// <c>workType</c>, <c>stage</c>) are absent by construction rather than
/// nulled — the record simply does not declare them, so a generic
/// session's summary cannot fabricate workflow identity.
/// </summary>
/// <remarks>
/// <see cref="ContextRefs"/> is the optional envelope of context
/// references (issue / epic / repository / workspace path) recorded on
/// the session metadata at launch; it is <c>null</c> when the session
/// carried no context reference, in line with "absent rather than null".
/// </remarks>
public sealed record GenericAgentSessionSummaryDto(
    string SessionId,
    string AgentId,
    string AgentName,
    [property: JsonPropertyName("runtimeSessionId")] string? RuntimeSessionId,
    [property: JsonPropertyName("runtime")] string? Runtime,
    [property: JsonPropertyName("status")] string Status,
    string CreatedAt,
    string? LastActivityAt,
    string? ResolvedModel,
    string? FailureCategory,
    int? ToolCallCount,
    int? ToolErrorCount,
    [property: JsonPropertyName("contextRefs")] GenericAgentSessionSummaryContextRefsDto? ContextRefs,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage,
    [property: JsonPropertyName("runtimeSessionLineage")] IReadOnlyList<RuntimeSessionLineageEntryDto>? RuntimeSessionLineage,
    [property: JsonPropertyName("recoveryAvailable")] bool RecoveryAvailable);

/// <summary>
/// Lightweight association entry returned by the issue/epic agent-session
/// association read endpoints
/// (<c>GET /api/projects/{projectRef}/issues/{number}/agent-sessions</c>
/// and <c>GET /api/projects/{projectRef}/epics/{epicRef}/agent-sessions</c>).
/// Issue-130 T-006: each entry carries the session id, the agent id and
/// agent name, the status, the created timestamp, and a link back to the
/// session summary route (<c>GET .../agent-sessions/{sessionId}</c>).
/// <see cref="SessionLink"/> is a relative URL path the client can use
/// to navigate to the session summary.
/// </summary>
public sealed record AgentSessionContextAssociationDto(
    string SessionId,
    string AgentId,
    string AgentName,
    string Status,
    string CreatedAt,
    string SessionLink);

/// <summary>
/// Optional envelope of context references recorded on a generic
/// AgentSession at launch (issue-130 T-003 / design D4). Each field is
/// null when the session carried no such reference; the envelope itself
/// is null when the session had no context references at all, mirroring
/// <see cref="AgentSessionListContextRefsDto"/> but kept as a distinct
/// type so the summary's wire shape evolves independently of the
/// agent-scoped list's.
/// </summary>
public sealed record GenericAgentSessionSummaryContextRefsDto(
    int? IssueNumber,
    int? EpicNumber,
    string? Repository,
    string? WorkspacePath);

public sealed record WorkflowSessionDetailDto(WorkflowSessionDto Session, AgentSessionTranscriptResponse Transcript);

public sealed record ActivityDto(
    ActivitySummaryDto Summary,
    IReadOnlyList<ActivityCardDto> Sessions,
    IReadOnlyList<ActivityWaitingCardDto> Waiting);

public sealed record ActivitySummaryDto(int Active, int Waiting, int Completed, int Failed, ActivitySlotUsageDto Slots);

public sealed record ActivitySlotUsageDto(int Active, int Max);

public sealed record ActivityCardDto(
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
    string? AgentId,
    string? AgentName,
    [property: JsonPropertyName("eventSummary")] AgentEventSummaryDto EventSummary,
    [property: JsonPropertyName("usage")] AgentUsageDto Usage);

public sealed record ActivityWorkItemDto(string Type, string Id, string Title, string? Stage, string? SessionWorkType);
public sealed record ActivityTaskProgressDto(int Completed, int Total);
public sealed record ActivityPreviewDto(string Kind, string Text, string CreatedAt);
public sealed record ActivityWaitingCardDto(int IssueNumber, string IssueTitle, string? Stage, string Label, string? RequestedAt, string? Preview);

public sealed record AgentSessionStatusRequest([property: JsonPropertyName("status")] string Status, DateTime? LastDataAt = null, string? FailureReason = null);

public sealed record AgentUsageTimeseriesDto(
    DateTime RangeFrom,
    DateTime RangeTo,
    string BucketGranularity,
    IReadOnlyList<UsageBucketDto> Buckets,
    IReadOnlyList<CumulativeCostPerShipPointDto>? CumulativeCostPerShip = null);

public sealed record CumulativeCostPerShipPointDto(
    DateTime DayEnd,
    double? CumulativeCost,
    string? Currency,
    int CumulativeShippedCount,
    double? CostPerShip);

public sealed record UsageBucketDto(
    DateTime BucketStart,
    DateTime BucketEnd,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    double CostAmount,
    string? CostCurrency);

public sealed record AgentCostMetricDto(
    double? Amount,
    string? Currency,
    int SampleCount);

/// <summary>
/// One windowed cost figure for the agent-cost surface. <see cref="Spend"/>
/// is the sum of per-session <c>UsageSummary.CostAmount</c> over sessions
/// whose creation time falls in the window; <see cref="PerIssueCost"/> is
/// the window's spend divided by the count of issues completed
/// (<see cref="IssueStatus.Done"/>) within the window. Each metric uses
/// the existing <see cref="AgentCostMetricDto"/> empty idiom
/// (<c>amount == null</c> ⟹ empty, distinct from a genuine <c>0.0</c>)
/// and the two emptiness states are evaluated independently per metric
/// per window (no sessions ⟹ empty spend; no completed issues ⟹ empty
/// per-issue cost). Strictly additive: existing cumulative rollup and
/// 7-day usage timeseries are unchanged.
/// </summary>
public sealed record AgentCostWindowedFigure(
    AgentCostMetricDto Spend,
    AgentCostMetricDto PerIssueCost);

/// <summary>
/// Response shape for the agent-cost rollup endpoint. The existing
/// cumulative rollup (<see cref="TotalCost"/>, <see cref="TodayCost"/>,
/// <see cref="DoneIssuesCount"/>, <see cref="CostPerShip"/>) and the
/// existing 7-day usage timeseries are preserved byte-for-byte;
/// <see cref="CurrentWindow"/> and <see cref="PreviousWindow"/> are
/// strictly additive — current-window and previous-adjacent-window
/// (same length, immediately preceding) spend and per-issue cost for
/// trend derivation.
/// </summary>
public sealed record AgentCostRollupDto(
    AgentCostMetricDto TotalCost,
    AgentCostMetricDto TodayCost,
    int DoneIssuesCount,
    AgentCostMetricDto CostPerShip,
    AgentCostWindowedFigure CurrentWindow,
    AgentCostWindowedFigure PreviousWindow);

public sealed record AgentCostRollupRawData(
    AgentCostMetricDto TotalCost,
    AgentCostMetricDto TodayCost);

/// <summary>
/// The windowed cost figures produced by
/// <see cref="AgentOps.Services.AgentUsageReporter.GetCostWindowedAsync"/>. Both windows
/// are 30 days; the previous window is the same length as the current
/// window and immediately precedes it. Both advance with the current
/// time. <see cref="CurrentSpend"/> is the sum of in-window per-session
/// <c>UsageSummary.CostAmount</c> values; <see cref="PreviousSpend"/>
/// is the same over the previous window.
/// <see cref="CurrentCompletedIssueCount"/> /
/// <see cref="PreviousCompletedIssueCount"/> are the in-window
/// completed-issue counts; per-issue cost is spend / count. Each
/// emptiness state is evaluated independently per metric per window.
/// </summary>
public sealed record AgentCostWindowedData(
    AgentCostWindowedFigure CurrentWindow,
    AgentCostWindowedFigure PreviousWindow);
