using System.Text.Json;
using Mohist.Server.Contracts;
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
    VerificationLanesView? VerificationLanes = null);

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
    TaskLaneView? Lane = null,
    string? AgentJobId = null,
    string? AgentSessionId = null);

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

/// <summary>
/// Per-task verification-lane view derived from the additive
/// <c>WorkflowActionAttempt.Lane</c> metadata. Populated only for tasks whose
/// <c>DefinitionId</c> is a recognized built-in lane id; non-lane tasks
/// (including the <c>recover:fix-ci</c> helper) project a null
/// <c>Lane</c> on the task view.
/// </summary>
[GenerateSerializer]
public sealed record TaskLaneView(
    string LaneId,
    int Order,
    int ConfiguredBudgetMs,
    string Outcome,
    string ActionAttemptId,
    string? WorkId = null,
    string? Detail = null,
    ExecutionError? Error = null,
    DateTimeOffset? FinishedAt = null);

/// <summary>
/// Build-stage verification-lane projection for a lane-enabled run.
/// Always contains all six catalog lanes, including pending or missing
/// lanes; the projection is null on legacy runs without lane fields so
/// they remain readable and are not asked to wait for synthesized state.
/// </summary>
[GenerateSerializer]
public sealed record VerificationLanesView(
    bool AllPassing,
    string? FirstNonPassingLane,
    IReadOnlyList<VerificationLaneView> Lanes);

/// <summary>
/// Single-lane view entry inside the build-stage verification projection.
/// <c>Outcome</c> uses the wire values <c>pending</c>, <c>pass</c>,
/// <c>fail</c>, or <c>timeout</c>; the same enum is shared with
/// <c>TaskLaneView</c> so the two projections stay consistent.
/// </summary>
[GenerateSerializer]
public sealed record VerificationLaneView(
    string LaneId,
    int Order,
    int ConfiguredBudgetMs,
    string Outcome,
    string ActionAttemptId,
    string? WorkId = null,
    string? Detail = null,
    ExecutionError? Error = null,
    DateTimeOffset? FinishedAt = null);

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
