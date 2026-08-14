using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

[GenerateSerializer]
public sealed record MetadataView(
    string? Name = null,
    Dictionary<string, string>? Labels = null,
    Dictionary<string, string>? Annotations = null,
    DateTimeOffset? CreatedAt = null);

[GenerateSerializer]
public sealed record WorkflowStatusView(
    string WorkflowRunId,
    string Status,
    string? CurrentStage,
    List<StageStatusView> Stages,
    PendingWorkView? PendingWork,
    FailureStatusView? Failure,
    List<AvailableActionView> AvailableActions,
    string? AssignedTo = null,
    MetadataView? Metadata = null,
    AgentResultAttentionView? AgentResultAttention = null);

/// <summary>
/// Non-failure attention for a Workflow-owned Agent task whose result could
/// not be confirmed before its settlement deadline. Populated only while the
/// settlement is blocked; a late authoritative result clears it by settling
/// the task through the normal terminal path.
/// </summary>
[GenerateSerializer]
public sealed record AgentResultAttentionView(
    string State,
    string Reason,
    string Message,
    DateTimeOffset DeadlineAt,
    string TaskRunId,
    string WorkId,
    string? RunnerId = null,
    string? AgentSessionId = null,
    string? AgentTurnId = null,
    string? NextAction = null,
    IReadOnlyList<string>? RecoveryActions = null);

/// <summary>
/// The durable result-settlement state for an Agent task attempt. This is
/// present while the task is awaiting a result, unknown, or blocked; the
/// aggregate clears it only after an authoritative result wins.
/// </summary>
[GenerateSerializer]
public sealed record AgentResultSettlementView(
    string State,
    string? Reason,
    string? Message,
    DateTimeOffset? FirstUnknownAt,
    DateTimeOffset? DeadlineAt,
    string TaskRunId,
    string WorkId,
    string? RunnerId = null,
    string? AgentSessionId = null,
    string? AgentTurnId = null,
    string? Runtime = null,
    string? RuntimeSessionId = null,
    string? StopOperationId = null,
    string? NextAction = null,
    IReadOnlyList<string>? RecoveryActions = null);

[GenerateSerializer]
public sealed record StageStatusView(
    string Stage,
    string Status,
    int Order,
    List<TaskStatusView> Tasks,
    List<CheckStatusView> Checks,
    ApprovalStatusView? ApprovalStatus,
    FailureStatusView? Failure,
    IReadOnlyList<StageFeedbackView>? Feedback = null);

[GenerateSerializer]
public sealed record StageFeedbackView(
    string Id,
    string Body,
    ApprovalFeedbackStatus Status,
    DateTimeOffset CreatedAt,
    StageFeedbackResolution? Resolution = null);

[GenerateSerializer]
public sealed record StageFeedbackResolution(
    string? ResolutionTaskId,
    DateTimeOffset? ResolvedAt,
    string? ResolutionSummary);

[GenerateSerializer]
public sealed record FailureStatusView(
    string Reason,
    string? Stage,
    string? TaskId,
    string? CheckName,
    string? Message,
    ExecutionError? Error = null);

[GenerateSerializer]
public sealed record AvailableActionView(
    string Name,
    string Label,
    string? Target);

public enum TaskClassification { UserFacing, Orchestration }

[GenerateSerializer]
public sealed record WorkflowTaskRequiredFile(
    string Path,
    string Source,
    bool CanFetchContent,
    string[]? Markers = null,
    string[]? OneOf = null,
    string? FailIf = null);

[GenerateSerializer]
public sealed record WorkflowStageProgress(
    string Stage,
    int Total,
    int Completed,
    int Running,
    int Failed,
    string? CurrentTaskTitle = null);

[GenerateSerializer]
public sealed record ArtifactSummaryView(
    string ArtifactId,
    string Path,
    string Kind,
    string? DisplayName,
    DateTimeOffset RecordedAt,
    long? Size);

[GenerateSerializer]
public sealed record TaskStatusView(
    string Id,
    string Title,
    string? Uses,
    string Status,
    IReadOnlyList<WorkflowTaskRequiredFile>? RequiredFiles = null,
    TaskClassification Classification = TaskClassification.UserFacing,
    string? SessionName = null,
    IReadOnlyList<ArtifactSummaryView>? ArtifactSummaries = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    long? DurationMs = null,
    JsonElement? Output = null,
    ExecutionError? Error = null,
    AgentResultSettlementView? AgentResultSettlement = null);

[GenerateSerializer]
public sealed record CheckStatusView(
    string Name,
    string Title,
    string? Uses,
    string Status,
    string? Message,
    ExecutionError? Error = null);

[GenerateSerializer]
public sealed record PendingWorkView(
    string WorkId,
    string WorkType,
    string? Stage,
    string? Title,
    string? Uses);

[GenerateSerializer]
public sealed record ApprovalStatusView(
    string? Result,
    string RequestedAt,
    string? RespondedAt,
    string? DecidedBy = null,
    string? DisplayName = null);

/// <summary>
/// Minimal associated-issue reference surfaced by the
/// <c>GET /api/workflow-runs/{workflowRunId}</c> read model: a project-scoped
/// human-numbered handle plus the title, without full issue details.
/// </summary>
public sealed record WorkflowRunIssueRef(
    string ProjectId,
    int Number,
    string Title);
