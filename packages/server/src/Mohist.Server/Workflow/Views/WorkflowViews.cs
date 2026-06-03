using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Views;

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
    string? ClaimedBy = null,
    MetadataView? Metadata = null);

[GenerateSerializer]
public sealed record StageStatusView(
    string Stage,
    string Status,
    int Order,
    List<TaskStatusView> Tasks,
    List<CheckStatusView> Checks,
    ApprovalStatusView? ApprovalStatus,
    FailureStatusView? Failure);

[GenerateSerializer]
public sealed record FailureStatusView(
    string Reason,
    string? Stage,
    string? TaskId,
    string? CheckName,
    string? Message);

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
    string[]? Markers = null);

[GenerateSerializer]
public sealed record WorkflowStageProgress(
    string Stage,
    int Total,
    int Completed,
    int Running,
    int Failed,
    string? CurrentTaskTitle = null);

[GenerateSerializer]
public sealed record TaskStatusView(
    string Id,
    string Title,
    string? Uses,
    string Status,
    IReadOnlyList<WorkflowTaskRequiredFile>? RequiredFiles = null,
    TaskClassification Classification = TaskClassification.UserFacing);

[GenerateSerializer]
public sealed record CheckStatusView(
    string Name,
    string Title,
    string? Uses,
    string Status,
    string? Message);

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
    string? RespondedAt);

[GenerateSerializer]
public sealed record WorkflowVariablesView(
    string Variables,
    Dictionary<string, Dictionary<string, string>>? StageVariables = null);