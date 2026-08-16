using System.Text.Json;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Domain.Run;

/// <summary>
/// The runner's initial workspace arbitration. The value is intentionally a
/// wire string because the runner uses kebab-case values on the HTTP contract.
/// </summary>
public static class WorkflowTaskWorkspaceOutcomes
{
    public const string CommittedClean = "committed-clean";
    public const string Dirty = "dirty";
    public const string Unconfirmed = "unconfirmed";

    public static bool IsKnown(string? value) => value is
        CommittedClean or Dirty or Unconfirmed;
}

public enum WorkflowTaskRecoveryState
{
    Dirty,
    Unconfirmed,
}

public static class WorkflowTaskRecoveryActions
{
    public const string Cleanup = "cleanup";
    public const string Inspect = "inspect";
    public const string Verify = "workspace-verification";
    public const string AdoptTaskSourceChanges = "adopt-task-source-changes";
    public const string AllocateFreshWorkspace = "allocate-fresh-workspace";
    public const string Stop = "stop";

    public static readonly IReadOnlyList<string> All =
    [
        Cleanup,
        Inspect,
        Verify,
        AdoptTaskSourceChanges,
        AllocateFreshWorkspace,
        Stop,
    ];
}

/// <summary>
/// Exact identity of one Workflow task execution. Workspace generation is a
/// JSON scalar because runner generations are numeric in some registries and
/// opaque strings in others; it is never coerced by the server.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowTaskExecutionIdentity(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string? Stage,
    [property: Id(2)] string TaskAttemptId,
    [property: Id(3)] string WorkId,
    [property: Id(4)] string OwnerKind,
    [property: Id(5)] string OwnerId,
    [property: Id(6)] string RunnerId,
    [property: Id(7)] string? WorkspaceId,
    [property: Id(8)] JsonElement? WorkspaceGeneration);

[GenerateSerializer]
public sealed record ActionCompletion(
    [property: Id(0)] int Version,
    [property: Id(1)] bool ActionStarted,
    [property: Id(2)] string Outcome,
    [property: Id(3)] string Phase,
    [property: Id(4)] JsonElement? Output,
    [property: Id(5)] ExecutionError? Error,
    [property: Id(6)] IReadOnlyList<string> ArtifactUploadIds,
    [property: Id(7)] JsonElement? CapturedOutputs,
    [property: Id(8)] DateTimeOffset CompletedAt);

/// <summary>
/// Immutable initial Git/workspace evidence. Cleanup and later verification
/// are deliberately separate records and cannot rewrite this value.
/// </summary>
[GenerateSerializer]
public sealed record CommitReceipt(
    [property: Id(0)] int Version,
    [property: Id(1)] WorkflowTaskExecutionIdentity Identity,
    [property: Id(2)] string? ExpectedBranch,
    [property: Id(3)] string? ExpectedHead,
    [property: Id(4)] string? ExpectedTree,
    [property: Id(5)] string? ObservedBranch,
    [property: Id(6)] string? ObservedHead,
    [property: Id(7)] string? ObservedTree,
    [property: Id(8)] IReadOnlyList<string> Staged,
    [property: Id(9)] IReadOnlyList<string> Unstaged,
    [property: Id(10)] IReadOnlyList<string> Untracked,
    [property: Id(11)] bool Authoritative,
    [property: Id(12)] string? Reason,
    [property: Id(13)] DateTimeOffset ProbedAt);

[GenerateSerializer]
public sealed record WorkflowTaskCompletionBoundary(
    [property: Id(0)] int Version,
    [property: Id(1)] WorkflowTaskExecutionIdentity Identity,
    [property: Id(2)] ActionCompletion ActionCompletion,
    [property: Id(3)] CommitReceipt CommitReceipt,
    [property: Id(4)] string WorkspaceOutcome,
    [property: Id(5)] string? WorkspaceReason,
    [property: Id(6)] string Fingerprint);

/// <summary>
/// The report payload retained between durable admission and projection. It
/// is not the completion boundary: it is replay input for the projection
/// transaction and can be discarded after that transaction commits.
/// </summary>
[GenerateSerializer]
public sealed record WorkflowTaskReportProjection(
    [property: Id(0)] TaskReportStatus Status,
    [property: Id(1)] JsonElement? Output,
    [property: Id(2)] string? Detail,
    [property: Id(3)] IReadOnlyList<RuntimeTaskInput>? AddTasks,
    [property: Id(4)] ExecutionError? Error,
    [property: Id(5)] IReadOnlyList<string>? ArtifactUploadIds,
    [property: Id(6)] IReadOnlyList<ArtifactRef>? BoundArtifacts = null,
    [property: Id(7)] bool ArtifactEventsApplied = false)
{
    public static WorkflowTaskReportProjection From(TaskReport report) => new(
        report.Status,
        report.Output,
        report.Detail,
        report.AddTasks,
        report.Error,
        report.ArtifactUploadIds,
        report.Artifacts);

    public WorkflowTaskReportProjection WithBoundArtifacts(IReadOnlyList<ArtifactRef> artifacts) =>
        this with { BoundArtifacts = artifacts, ArtifactUploadIds = null };

    public TaskReport ToReport(string workId, string taskRunId, bool includeArtifactEvents) => new(
        workId,
        Status,
        Output,
        includeArtifactEvents ? BoundArtifacts : null,
        Detail,
        AddTasks,
        Error,
        ArtifactUploadIds,
        taskRunId);
}

[GenerateSerializer]
public sealed record WorkspaceVerification(
    [property: Id(0)] string IdempotencyKey,
    [property: Id(1)] WorkflowTaskExecutionIdentity Identity,
    [property: Id(2)] string BoundaryFingerprint,
    [property: Id(3)] string? ObservedBranch,
    [property: Id(4)] string? ObservedHead,
    [property: Id(5)] string? ObservedTree,
    [property: Id(6)] IReadOnlyList<string> Staged,
    [property: Id(7)] IReadOnlyList<string> Unstaged,
    [property: Id(8)] IReadOnlyList<string> Untracked,
    [property: Id(9)] bool Authoritative,
    [property: Id(10)] string? Reason,
    [property: Id(11)] string? Verifier,
    [property: Id(12)] string? Source,
    [property: Id(13)] string? SourceAdoptionOperationId = null);

[GenerateSerializer]
public sealed record WorkflowTaskProjectionProgress(
    [property: Id(0)] bool Accepted,
    [property: Id(1)] bool Applied,
    [property: Id(2)] DateTimeOffset? AcceptedAt,
    [property: Id(3)] DateTimeOffset? AppliedAt);

/// <summary>
/// Mutable recovery state for a dirty or unconfirmed completion. It owns
/// verification and projection progress; the completion boundary remains
/// immutable on the TaskRun.
/// </summary>
[GenerateSerializer]
public sealed class WorkflowTaskRecovery
{
    [Id(0)] public required WorkflowTaskRecoveryState State { get; set; }
    [Id(1)] public required string BoundaryFingerprint { get; init; }
    [Id(2)] public required WorkflowTaskExecutionIdentity Identity { get; init; }
    [Id(3)] public required string Reason { get; set; }
    [Id(4)] public DateTimeOffset? DeadlineAt { get; set; }
    [Id(5)] public required string NextAction { get; set; }
    [Id(6)] public JsonElement? Output { get; set; }
    [Id(7)] public IReadOnlyList<ArtifactRef>? Artifacts { get; set; }
    [Id(8)] public IReadOnlyList<string>? ArtifactUploadIds { get; set; }
    [Id(9)] public IReadOnlyList<string>? CleanupScope { get; init; }
    [Id(10)] public WorkflowTaskProjectionProgress Projection { get; set; } = new(false, false, null, null);
    [Id(11)] public List<WorkspaceVerification> Verifications { get; set; } = [];

    public static WorkflowTaskRecovery Create(
        WorkflowTaskCompletionBoundary boundary,
        DateTimeOffset acceptedAt)
    {
        var state = boundary.WorkspaceOutcome == WorkflowTaskWorkspaceOutcomes.Dirty
            ? WorkflowTaskRecoveryState.Dirty
            : WorkflowTaskRecoveryState.Unconfirmed;
        return new WorkflowTaskRecovery
        {
            State = state,
            BoundaryFingerprint = boundary.Fingerprint,
            Identity = boundary.Identity,
            Reason = boundary.WorkspaceReason
                ?? (state == WorkflowTaskRecoveryState.Dirty
                    ? "workspace-status-non-empty"
                    : "workspace-evidence-unavailable"),
            NextAction = state == WorkflowTaskRecoveryState.Dirty
                ? WorkflowTaskRecoveryActions.Inspect
                : WorkflowTaskRecoveryActions.Verify,
            Output = boundary.ActionCompletion.Output,
            ArtifactUploadIds = boundary.ActionCompletion.ArtifactUploadIds,
            Projection = new WorkflowTaskProjectionProgress(true, false, acceptedAt, null),
        };
    }

    public WorkspaceVerification? FindVerification(string idempotencyKey) =>
        Verifications.SingleOrDefault(v => string.Equals(v.IdempotencyKey, idempotencyKey, StringComparison.Ordinal));
}

public static class WorkflowTaskCompletionBoundaryRules
{
    public static bool MatchesExpectedIdentity(
        WorkflowTaskExecutionIdentity expected,
        WorkflowTaskExecutionIdentity actual) =>
        string.Equals(expected.WorkflowRunId, actual.WorkflowRunId, StringComparison.Ordinal)
        && string.Equals(expected.Stage, actual.Stage, StringComparison.Ordinal)
        && string.Equals(expected.TaskAttemptId, actual.TaskAttemptId, StringComparison.Ordinal)
        && string.Equals(expected.WorkId, actual.WorkId, StringComparison.Ordinal)
        && string.Equals(expected.OwnerKind, actual.OwnerKind, StringComparison.Ordinal)
        && string.Equals(expected.OwnerId, actual.OwnerId, StringComparison.Ordinal)
        && string.Equals(expected.RunnerId, actual.RunnerId, StringComparison.Ordinal)
        && (expected.WorkspaceId is null
            || string.Equals(expected.WorkspaceId, actual.WorkspaceId, StringComparison.Ordinal))
        && (!expected.WorkspaceGeneration.HasValue
            || SameGeneration(expected.WorkspaceGeneration, actual.WorkspaceGeneration));

    public static bool SameIdentity(
        WorkflowTaskExecutionIdentity left,
        WorkflowTaskExecutionIdentity right) =>
        string.Equals(left.WorkflowRunId, right.WorkflowRunId, StringComparison.Ordinal)
        && string.Equals(left.Stage, right.Stage, StringComparison.Ordinal)
        && string.Equals(left.TaskAttemptId, right.TaskAttemptId, StringComparison.Ordinal)
        && string.Equals(left.WorkId, right.WorkId, StringComparison.Ordinal)
        && string.Equals(left.OwnerKind, right.OwnerKind, StringComparison.Ordinal)
        && string.Equals(left.OwnerId, right.OwnerId, StringComparison.Ordinal)
        && string.Equals(left.RunnerId, right.RunnerId, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
        && SameGeneration(left.WorkspaceGeneration, right.WorkspaceGeneration);

    public static bool SameGeneration(JsonElement? left, JsonElement? right)
    {
        if (!left.HasValue || !right.HasValue)
            return !left.HasValue && !right.HasValue;
        return left.Value.GetRawText() == right.Value.GetRawText();
    }

    public static bool MatchesReport(
        TaskReport report,
        WorkflowTaskCompletionBoundary boundary) =>
        SameJson(report.Output, boundary.ActionCompletion.Output)
        && SameError(report.Error, boundary.ActionCompletion.Error)
        && (report.ArtifactUploadIds ?? []).SequenceEqual(
            boundary.ActionCompletion.ArtifactUploadIds,
            StringComparer.Ordinal)
        && (report.WorkspaceOutcome is null
            || string.Equals(report.WorkspaceOutcome, boundary.WorkspaceOutcome, StringComparison.Ordinal))
        && (report.WorkspaceReason is null
            || string.Equals(report.WorkspaceReason, boundary.WorkspaceReason, StringComparison.Ordinal));

    public static bool SameBoundary(
        WorkflowTaskCompletionBoundary left,
        WorkflowTaskCompletionBoundary right) =>
        string.Equals(left.Fingerprint, right.Fingerprint, StringComparison.Ordinal)
        && left.Version == right.Version
        && SameIdentity(left.Identity, right.Identity)
        && SameCompletion(left.ActionCompletion, right.ActionCompletion)
        && SameReceipt(left.CommitReceipt, right.CommitReceipt)
        && string.Equals(left.WorkspaceOutcome, right.WorkspaceOutcome, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceReason, right.WorkspaceReason, StringComparison.Ordinal);

    public static bool SameCompletion(ActionCompletion left, ActionCompletion right) =>
        left.Version == right.Version
        && left.ActionStarted == right.ActionStarted
        && string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal)
        && string.Equals(left.Phase, right.Phase, StringComparison.Ordinal)
        && SameJson(left.Output, right.Output)
        && SameError(left.Error, right.Error)
        && left.ArtifactUploadIds.SequenceEqual(right.ArtifactUploadIds, StringComparer.Ordinal)
        && SameJson(left.CapturedOutputs, right.CapturedOutputs)
        && left.CompletedAt == right.CompletedAt;

    public static bool SameReceipt(CommitReceipt left, CommitReceipt right) =>
        left.Version == right.Version
        && SameIdentity(left.Identity, right.Identity)
        && string.Equals(left.ExpectedBranch, right.ExpectedBranch, StringComparison.Ordinal)
        && string.Equals(left.ExpectedHead, right.ExpectedHead, StringComparison.Ordinal)
        && string.Equals(left.ExpectedTree, right.ExpectedTree, StringComparison.Ordinal)
        && string.Equals(left.ObservedBranch, right.ObservedBranch, StringComparison.Ordinal)
        && string.Equals(left.ObservedHead, right.ObservedHead, StringComparison.Ordinal)
        && string.Equals(left.ObservedTree, right.ObservedTree, StringComparison.Ordinal)
        && left.Staged.SequenceEqual(right.Staged, StringComparer.Ordinal)
        && left.Unstaged.SequenceEqual(right.Unstaged, StringComparer.Ordinal)
        && left.Untracked.SequenceEqual(right.Untracked, StringComparer.Ordinal)
        && left.Authoritative == right.Authoritative
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && left.ProbedAt == right.ProbedAt;

    public static bool SameError(ExecutionError? left, ExecutionError? right) =>
        left is null && right is null
        || left is not null && right is not null
            && string.Equals(left.Code, right.Code, StringComparison.Ordinal)
            && string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    public static bool SameJson(JsonElement? left, JsonElement? right) =>
        left.HasValue && right.HasValue
            ? left.Value.GetRawText() == right.Value.GetRawText()
            : !left.HasValue && !right.HasValue;

    public static bool IsClean(WorkflowTaskCompletionBoundary boundary) =>
        string.Equals(boundary.WorkspaceOutcome, WorkflowTaskWorkspaceOutcomes.CommittedClean, StringComparison.Ordinal)
        && boundary.CommitReceipt.Authoritative
        && boundary.CommitReceipt.Reason is null
        && boundary.CommitReceipt.Staged.Count == 0
        && boundary.CommitReceipt.Unstaged.Count == 0
        && boundary.CommitReceipt.Untracked.Count == 0
        && boundary.Identity.WorkspaceId is not null
        && boundary.Identity.WorkspaceGeneration.HasValue
        && boundary.CommitReceipt.ExpectedBranch is not null
        && boundary.CommitReceipt.ExpectedHead is not null
        && boundary.CommitReceipt.ExpectedTree is not null
        && string.Equals(boundary.CommitReceipt.ObservedBranch, boundary.CommitReceipt.ExpectedBranch, StringComparison.Ordinal)
        && string.Equals(boundary.CommitReceipt.ObservedHead, boundary.CommitReceipt.ExpectedHead, StringComparison.Ordinal)
        && string.Equals(boundary.CommitReceipt.ObservedTree, boundary.CommitReceipt.ExpectedTree, StringComparison.Ordinal);

    public static bool IsDirty(WorkflowTaskCompletionBoundary boundary) =>
        string.Equals(boundary.WorkspaceOutcome, WorkflowTaskWorkspaceOutcomes.Dirty, StringComparison.Ordinal)
        && boundary.CommitReceipt.Authoritative
        && boundary.Identity.WorkspaceId is not null
        && boundary.Identity.WorkspaceGeneration.HasValue
        && boundary.CommitReceipt.ExpectedBranch is not null
        && boundary.CommitReceipt.ExpectedHead is not null
        && boundary.CommitReceipt.ExpectedTree is not null
        && string.Equals(boundary.CommitReceipt.ObservedBranch, boundary.CommitReceipt.ExpectedBranch, StringComparison.Ordinal)
        && string.Equals(boundary.CommitReceipt.ObservedHead, boundary.CommitReceipt.ExpectedHead, StringComparison.Ordinal)
        && string.Equals(boundary.CommitReceipt.ObservedTree, boundary.CommitReceipt.ExpectedTree, StringComparison.Ordinal)
        && (boundary.CommitReceipt.Staged.Count > 0
            || boundary.CommitReceipt.Unstaged.Count > 0
            || boundary.CommitReceipt.Untracked.Count > 0);

    public static bool IsUnconfirmed(WorkflowTaskCompletionBoundary boundary) =>
        string.Equals(boundary.WorkspaceOutcome, WorkflowTaskWorkspaceOutcomes.Unconfirmed, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(boundary.WorkspaceReason ?? boundary.CommitReceipt.Reason);
}
