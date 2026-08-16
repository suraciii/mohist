using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    private async Task<ReportAck> AcceptWorkflowTaskBoundaryAsync(
        string workerId,
        string workId,
        TaskReport report,
        WorkflowActiveWork activeWork,
        TaskRun task)
    {
        var boundary = report.CompletionBoundary;
        if (boundary is null)
            return ReportAck.Stale;

        if (!ValidateWorkflowTaskBoundary(workerId, workId, report, activeWork, task, boundary))
            return ReportAck.Stale;

        if (task.CompletionBoundary is { } existing)
        {
            return WorkflowTaskCompletionBoundaryRules.SameBoundary(existing, boundary)
                ? ReportAck.Accepted
                : ReportAck.Stale;
        }

        var now = Now();
        task.CompletionBoundary = boundary;
        task.PendingCompletionReport = WorkflowTaskReportProjection.From(report);
        task.CompletionProjectionApplied = false;
        task.AgentResultSettlement = null;
        task.Interruption = null;

        var actionFailed = string.Equals(
            boundary.ActionCompletion.Outcome,
            "failed",
            StringComparison.Ordinal);
        var needsRecovery = !actionFailed
            && (boundary.WorkspaceOutcome is not WorkflowTaskWorkspaceOutcomes.CommittedClean
                || string.Equals(boundary.ActionCompletion.Outcome, "unknown", StringComparison.Ordinal));
        task.WorkflowTaskRecovery = needsRecovery
            ? WorkflowTaskRecovery.Create(boundary, now)
            : null;

        // This is the completion-boundary transaction. No artifact binding,
        // task event, variable write, or status projection is allowed before
        // this save succeeds.
        await SaveRunAsync();

        if (task.WorkflowTaskRecovery is not null)
        {
            await PreserveRecoveryArtifactsAsync(activeWork, task);
            return ReportAck.Accepted;
        }

        await ProjectAcceptedWorkflowTaskAsync(activeWork, task);
        return ReportAck.Accepted;
    }

    private bool ValidateWorkflowTaskBoundary(
        string workerId,
        string workId,
        TaskReport report,
        WorkflowActiveWork activeWork,
        TaskRun task,
        WorkflowTaskCompletionBoundary boundary)
    {
        if (_run is null
            || boundary.Version != 1
            || string.IsNullOrWhiteSpace(boundary.Fingerprint)
            || !WorkflowTaskWorkspaceOutcomes.IsKnown(boundary.WorkspaceOutcome)
            || !string.Equals(boundary.Identity.WorkflowRunId, GrainKey, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.Stage, activeWork.Item.Stage, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.TaskAttemptId, task.Id, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.OwnerKind, WorkDispatchOwnerKinds.Workflow, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.OwnerId, GrainKey, StringComparison.Ordinal)
            || !string.Equals(boundary.Identity.RunnerId, workerId, StringComparison.Ordinal)
            || !WorkflowTaskCompletionBoundaryRules.SameIdentity(
                boundary.Identity,
                boundary.CommitReceipt.Identity))
        {
            return false;
        }

        if (!string.Equals(report.WorkId, workId, StringComparison.Ordinal)
            || (report.WorkspaceOutcome is not null
                && !string.Equals(report.WorkspaceOutcome, boundary.WorkspaceOutcome, StringComparison.Ordinal))
            || (report.WorkspaceReason is not null
                && !string.Equals(report.WorkspaceReason, boundary.WorkspaceReason, StringComparison.Ordinal))
            || !WorkflowTaskCompletionBoundaryRules.SameJson(report.Output, boundary.ActionCompletion.Output)
            || !WorkflowTaskCompletionBoundaryRules.SameError(report.Error, boundary.ActionCompletion.Error)
            || !(report.ArtifactUploadIds ?? []).SequenceEqual(
                boundary.ActionCompletion.ArtifactUploadIds,
                StringComparer.Ordinal))
        {
            return false;
        }

        var actionOutcome = boundary.ActionCompletion.Outcome;
        var statusMatches = actionOutcome switch
        {
            "failed" => report.Status == TaskReportStatus.Failed,
            "succeeded" or "unknown" => report.Status == TaskReportStatus.Succeeded,
            _ => false,
        };
        if (!statusMatches)
            return false;

        if (task.ActiveExecutionIdentity is { } expectedIdentity
            && !WorkflowTaskCompletionBoundaryRules.MatchesExpectedIdentity(expectedIdentity, boundary.Identity))
        {
            return false;
        }

        if (boundary.ActionCompletion.ArtifactUploadIds is null
            || boundary.CommitReceipt.Staged is null
            || boundary.CommitReceipt.Unstaged is null
            || boundary.CommitReceipt.Untracked is null
            || string.IsNullOrWhiteSpace(boundary.ActionCompletion.Phase)
            || boundary.CommitReceipt.ProbedAt == default)
        {
            return false;
        }

        var receipt = boundary.CommitReceipt;
        if (boundary.WorkspaceOutcome == WorkflowTaskWorkspaceOutcomes.CommittedClean
            && !WorkflowTaskCompletionBoundaryRules.IsClean(boundary))
        {
            return false;
        }
        if (boundary.WorkspaceOutcome == WorkflowTaskWorkspaceOutcomes.Dirty
            && !WorkflowTaskCompletionBoundaryRules.IsDirty(boundary))
        {
            return false;
        }
        if (boundary.WorkspaceOutcome == WorkflowTaskWorkspaceOutcomes.Unconfirmed
            && !WorkflowTaskCompletionBoundaryRules.IsUnconfirmed(boundary))
        {
            return false;
        }

        var expectedWorkspace = _run.Workspace;
        if (expectedWorkspace is not null)
        {
            if (expectedWorkspace.WorkspaceId is not null
                && !string.Equals(expectedWorkspace.WorkspaceId, boundary.Identity.WorkspaceId, StringComparison.Ordinal))
                return false;
            if (expectedWorkspace.WorkspaceGeneration.HasValue
                && !WorkflowTaskCompletionBoundaryRules.SameGeneration(
                    expectedWorkspace.WorkspaceGeneration,
                    boundary.Identity.WorkspaceGeneration))
                return false;
            if (expectedWorkspace.Branch is not null
                && !string.Equals(expectedWorkspace.Branch, receipt.ExpectedBranch, StringComparison.Ordinal))
                return false;
            if (expectedWorkspace.Head is not null
                && !string.Equals(expectedWorkspace.Head, receipt.ExpectedHead, StringComparison.Ordinal))
                return false;
            if (expectedWorkspace.Tree is not null
                && !string.Equals(expectedWorkspace.Tree, receipt.ExpectedTree, StringComparison.Ordinal))
                return false;
        }

        // A clean or dirty result is a claim about an exact workspace. When
        // the active dispatch has no persisted workspace identity, only the
        // non-settling unconfirmed outcome is admissible.
        if ((boundary.WorkspaceOutcome is WorkflowTaskWorkspaceOutcomes.CommittedClean
            or WorkflowTaskWorkspaceOutcomes.Dirty)
            && (boundary.Identity.WorkspaceId is null
                || !boundary.Identity.WorkspaceGeneration.HasValue))
        {
            return false;
        }

        return true;
    }

    private async Task PreserveRecoveryArtifactsAsync(WorkflowActiveWork activeWork, TaskRun task)
    {
        var recovery = task.WorkflowTaskRecovery;
        var pending = task.PendingCompletionReport;
        if (recovery is null || pending is null || pending.ArtifactUploadIds is not { Count: > 0 })
            return;

        var report = pending.ToReport(activeWork.WorkId, task.Id, includeArtifactEvents: false);
        var bound = await BindTaskReportArtifactsAsync(activeWork, report);
        if (bound.ArtifactUploadIds is { Count: > 0 })
        {
            recovery.Reason = "artifact-binding-pending";
            recovery.NextAction = WorkflowTaskRecoveryActions.Verify;
            await SaveRunAsync();
            return;
        }

        task.PendingCompletionReport = WorkflowTaskReportProjection.From(bound);
        recovery.Artifacts = task.PendingCompletionReport.BoundArtifacts;
        recovery.ArtifactUploadIds = task.PendingCompletionReport.ArtifactUploadIds;
        await SaveRunAsync();
    }

    private async Task ProjectAcceptedWorkflowTaskAsync(WorkflowActiveWork activeWork, TaskRun task)
    {
        var pending = task.PendingCompletionReport;
        if (pending is null || task.CompletionBoundary is null)
            return;

        if (pending.ArtifactUploadIds is { Count: > 0 })
        {
            var report = pending.ToReport(activeWork.WorkId, task.Id, includeArtifactEvents: false);
            var bound = await BindTaskReportArtifactsAsync(activeWork, report);
            if (bound.ArtifactUploadIds is { Count: > 0 })
            {
                task.WorkflowTaskRecovery ??= CreateArtifactBindingRecovery(task);
                task.WorkflowTaskRecovery.Reason = "artifact-binding-pending";
                task.WorkflowTaskRecovery.NextAction = WorkflowTaskRecoveryActions.Verify;
                await SaveRunAsync();
                return;
            }

            pending = WorkflowTaskReportProjection.From(bound);
            task.PendingCompletionReport = pending;
            if (task.WorkflowTaskRecovery is not null)
            {
                task.WorkflowTaskRecovery.Artifacts = pending.BoundArtifacts;
                task.WorkflowTaskRecovery.ArtifactUploadIds = pending.ArtifactUploadIds;
            }
            await SaveRunAsync();
        }

        var effective = pending.ToReport(
            activeWork.WorkId,
            task.Id,
            includeArtifactEvents: !pending.ArtifactEventsApplied);
        IReadOnlyList<WorkflowEvent> events;
        try
        {
            events = await _workLifecycle.ApplyTaskReportAsync(
                _run!,
                effective,
                activeWork.Item.Stage,
                task.Id);
        }
        catch (InvalidOperationException ex) when (effective.Status == TaskReportStatus.Succeeded)
        {
            // The boundary is already durable. Invalid follow-up input is a
            // report-projection failure and retains the historical failed-task
            // behavior without replacing the immutable Action evidence.
            effective = effective with
            {
                Status = TaskReportStatus.Failed,
                Output = null,
                Artifacts = null,
                Detail = $"Recovery follow-up rejected: {ex.Message}",
                Error = new ExecutionError("invalid-follow-up", ex.Message),
            };
            task.PendingCompletionReport = WorkflowTaskReportProjection.From(effective);
            events = await _workLifecycle.ApplyTaskReportAsync(
                _run!,
                effective,
                activeWork.Item.Stage,
                task.Id);
        }

        task.PendingCompletionReport = pending with { ArtifactEventsApplied = true };
        task.CompletionProjectionApplied = true;
        if (task.WorkflowTaskRecovery is { } recovery)
        {
            recovery.Projection = recovery.Projection with
            {
                Applied = true,
                AppliedAt = Now(),
            };
            recovery.Reason = "verified-clean";
            recovery.NextAction = WorkflowTaskRecoveryActions.Inspect;
        }
        task.PendingCompletionReport = null;
        await CommitAsync(events);
        await DeleteSnapshotBestEffortAsync(activeWork.WorkId);
    }

    private static WorkflowTaskRecovery CreateArtifactBindingRecovery(TaskRun task) =>
        new()
        {
            State = WorkflowTaskRecoveryState.Unconfirmed,
            BoundaryFingerprint = task.CompletionBoundary!.Fingerprint,
            Identity = task.CompletionBoundary.Identity,
            Reason = "artifact-binding-pending",
            NextAction = WorkflowTaskRecoveryActions.Verify,
            Output = task.CompletionBoundary.ActionCompletion.Output,
            ArtifactUploadIds = task.CompletionBoundary.ActionCompletion.ArtifactUploadIds,
            Projection = new WorkflowTaskProjectionProgress(true, false, null, null),
        };

    private static bool SameVerification(WorkspaceVerification left, WorkspaceVerification right) =>
        string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
        && WorkflowTaskCompletionBoundaryRules.SameIdentity(left.Identity, right.Identity)
        && string.Equals(left.BoundaryFingerprint, right.BoundaryFingerprint, StringComparison.Ordinal)
        && string.Equals(left.ObservedBranch, right.ObservedBranch, StringComparison.Ordinal)
        && string.Equals(left.ObservedHead, right.ObservedHead, StringComparison.Ordinal)
        && string.Equals(left.ObservedTree, right.ObservedTree, StringComparison.Ordinal)
        && left.Staged.SequenceEqual(right.Staged, StringComparer.Ordinal)
        && left.Unstaged.SequenceEqual(right.Unstaged, StringComparer.Ordinal)
        && left.Untracked.SequenceEqual(right.Untracked, StringComparer.Ordinal)
        && left.Authoritative == right.Authoritative
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && string.Equals(left.Verifier, right.Verifier, StringComparison.Ordinal)
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
        && string.Equals(left.SourceAdoptionOperationId, right.SourceAdoptionOperationId, StringComparison.Ordinal);

    public async Task<ReportAck> ReceiveWorkspaceVerificationAsync(WorkspaceVerification verification)
    {
        RejectIfRunReloadRequired();
        if (_run is null
            || string.IsNullOrWhiteSpace(verification.IdempotencyKey)
            || !string.Equals(verification.Identity.WorkflowRunId, GrainKey, StringComparison.Ordinal))
            return ReportAck.Stale;

        var found = _run.Stages
            .SelectMany(stage => stage.Tasks.Select(task => (Stage: stage, Task: task)))
            .SingleOrDefault(candidate => string.Equals(candidate.Task.Id, verification.Identity.TaskAttemptId, StringComparison.Ordinal));
        if (found.Task is null)
            return ReportAck.Stale;

        var task = found.Task;
        var recovery = task.WorkflowTaskRecovery;
        var boundary = task.CompletionBoundary;
        if (recovery is null
            || boundary is null
            || !WorkflowTaskCompletionBoundaryRules.SameIdentity(boundary.Identity, verification.Identity)
            || !string.Equals(boundary.Fingerprint, verification.BoundaryFingerprint, StringComparison.Ordinal))
            return ReportAck.Stale;

        var existing = recovery.FindVerification(verification.IdempotencyKey);
        if (existing is not null)
            return SameVerification(existing, verification) ? ReportAck.Accepted : ReportAck.Stale;

        if (task.Status != TaskRunStatus.Running || recovery.Projection.Applied)
            return ReportAck.Stale;

        if (verification.SourceAdoptionOperationId is not null)
            return ReportAck.Stale;

        recovery.Verifications.Add(verification);
        var clean = verification.Authoritative
            && verification.Staged.Count == 0
            && verification.Unstaged.Count == 0
            && verification.Untracked.Count == 0
            && string.Equals(verification.ObservedBranch, boundary.CommitReceipt.ExpectedBranch, StringComparison.Ordinal)
            && string.Equals(verification.ObservedHead, boundary.CommitReceipt.ExpectedHead, StringComparison.Ordinal)
            && string.Equals(verification.ObservedTree, boundary.CommitReceipt.ExpectedTree, StringComparison.Ordinal);
        if (!clean)
        {
            recovery.Reason = verification.Reason ?? "workspace-verification-dirty";
            recovery.NextAction = WorkflowTaskRecoveryActions.Verify;
            await SaveRunAsync();
            return ReportAck.Accepted;
        }

        recovery.Reason = "verified-clean";
        recovery.NextAction = WorkflowTaskRecoveryActions.Inspect;
        await SaveRunAsync();
        var activeWork = WorkflowActiveWorkForTask(found.Stage, task);
        await ProjectAcceptedWorkflowTaskAsync(activeWork, task);
        return ReportAck.Accepted;
    }

    private static WorkflowActiveWork WorkflowActiveWorkForTask(StageRun stage, TaskRun task) =>
        new(
            WorkItem.Task(
                stage.Id,
                task.WorkId ?? task.Id,
                task.Title,
                task.Uses,
                task.WithInput,
                task.Artifacts,
                task.SetVars,
                task.Recovery,
                task.RecoveryRemaining,
                task.ExpectInput),
            task.Id);

    private async Task ReconcileWorkflowTaskCompletionAsync()
    {
        if (_run is null)
            return;

        var found = _run.Stages
            .SelectMany(stage => stage.Tasks.Select(task => (Stage: stage, Task: task)))
            .FirstOrDefault(candidate => candidate.Task.CompletionBoundary is not null
                && !candidate.Task.CompletionProjectionApplied
                && candidate.Task.WorkflowTaskRecovery is null);
        if (found.Task is null || found.Task.PendingCompletionReport is null)
            return;

        var active = WorkflowActiveWorkForTask(found.Stage, found.Task);
        await ProjectAcceptedWorkflowTaskAsync(active, found.Task);
    }
}
