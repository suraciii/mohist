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
            || boundary.CommitReceipt.ProbedAt == default
            || (boundary.CleanupScope ?? []).Any(path => !WorkflowRecoveryPathRules.IsSafeRelativePath(path)))
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
        && string.Equals(left.SourceAdoptionOperationId, right.SourceAdoptionOperationId, StringComparison.Ordinal)
        && string.Equals(left.Fence, right.Fence, StringComparison.Ordinal);

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

        if (task.Status != TaskRunStatus.Running
            || recovery.Projection.Applied
            || !HasCurrentRecoveryFence(recovery, verification.Identity, verification.BoundaryFingerprint, verification.Fence))
            return ReportAck.Stale;

        var adoption = verification.SourceAdoptionOperationId is null
            ? null
            : recovery.FindSourceAdoption(verification.SourceAdoptionOperationId);
        if (verification.SourceAdoptionOperationId is not null
            && (adoption is null || !adoption.Accepted || !adoption.Completed || adoption.ResultingHead is null))
            return ReportAck.Stale;

        recovery.Verifications.Add(verification);
        var sameOriginalTree = string.Equals(verification.ObservedHead, boundary.CommitReceipt.ExpectedHead, StringComparison.Ordinal)
            && string.Equals(verification.ObservedTree, boundary.CommitReceipt.ExpectedTree, StringComparison.Ordinal);
        var adoptedTree = adoption is not null
            && string.Equals(verification.ObservedHead, adoption.ResultingHead, StringComparison.Ordinal);
        var clean = verification.Authoritative
            && verification.Staged.Count == 0
            && verification.Unstaged.Count == 0
            && verification.Untracked.Count == 0
            && string.Equals(verification.ObservedBranch, boundary.CommitReceipt.ExpectedBranch, StringComparison.Ordinal)
            && (sameOriginalTree || adoptedTree);
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

    public async Task<WorkflowTaskCleanupLeaseResult> AcquireWorkflowTaskCleanupLeaseAsync(
        WorkflowTaskCleanupLeaseRequest request)
    {
        RejectIfRunReloadRequired();
        if (!TryFindRecovery(request.Identity, request.BoundaryFingerprint, out _, out var task, out var recovery, out _))
            return new(false, false, Reason: "recovery-identity-mismatch");
        if (string.IsNullOrWhiteSpace(request.OperationId))
            return new(false, false, Reason: "cleanup-operation-id-required");

        var requestedScope = NormalizePaths(request.CleanupScope ?? recovery.CleanupScope ?? []);
        if (requestedScope is null)
            return new(false, false, Reason: "cleanup-scope-invalid");
        var protectedOutputPaths = (task.Artifacts?.Files.Select(file => file.Path) ?? [])
            .Concat(task.PendingCompletionReport?.BoundArtifacts?.Select(artifact => artifact.Path) ?? [])
            .ToArray();
        if (requestedScope.Any(path => protectedOutputPaths.Any(protectedPath => WorkflowRecoveryPathRules.Overlaps(path, protectedPath))))
            return new(false, false, Reason: "cleanup-scope-overlaps-protected-output");
        var prior = recovery.FindCleanupLease(request.OperationId);
        if (prior is not null)
        {
            var same = WorkflowTaskCompletionBoundaryRules.SameIdentity(prior.Identity, request.Identity)
                && string.Equals(prior.BoundaryFingerprint, request.BoundaryFingerprint, StringComparison.Ordinal)
                && prior.CleanupScope.SequenceEqual(requestedScope, StringComparer.Ordinal);
            return same
                ? new(true, true, prior, Operation: recovery.FindCleanupOperation(request.OperationId))
                : new(false, false, Reason: "cleanup-operation-conflict");
        }

        var now = Now();
        if (recovery.CurrentCleanupLease is { } current && current.ExpiresAt > now)
            return new(false, false, Reason: "cleanup-lease-active");

        var budget = Math.Clamp(request.WorkBudget, 1, 128);
        var duration = request.LeaseDuration is { } requestedDuration
            ? requestedDuration
            : TimeSpan.FromMinutes(5);
        duration = duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duration;
        duration = duration > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : duration;
        var lease = new WorkflowTaskCleanupLease(
            request.OperationId,
            $"cleanup-fence:{Guid.NewGuid():N}",
            request.Identity,
            request.BoundaryFingerprint,
            requestedScope,
            now.Add(duration),
            budget,
            now);
        recovery.CleanupLeases.Add(lease);
        recovery.CurrentCleanupLease = lease;
        recovery.NextAction = WorkflowTaskRecoveryActions.Cleanup;
        await SaveRunAsync();
        return new(true, false, lease);
    }

    public async Task<WorkflowTaskCleanupOperationResult> RecordWorkflowTaskCleanupAsync(
        WorkflowTaskCleanupOperation operation)
    {
        RejectIfRunReloadRequired();
        if (!TryFindRecovery(operation.Identity, FindBoundaryFingerprint(operation.Identity), out _, out _, out var recovery, out _))
            return new(false, false, Reason: "recovery-identity-mismatch");
        var existing = recovery.FindCleanupOperation(operation.OperationId);
        if (existing is not null)
            return SameCleanupOperation(existing, operation)
                ? new(true, true, existing)
                : new(false, false, Reason: "cleanup-operation-conflict");
        var lease = recovery.CurrentCleanupLease;
        if (lease is null
            || lease.ExpiresAt <= Now()
            || !string.Equals(lease.Fence, operation.Fence, StringComparison.Ordinal)
            || !WorkflowTaskCompletionBoundaryRules.SameIdentity(lease.Identity, operation.Identity))
            return new(false, false, Reason: "cleanup-fence-stale");
        if (operation.Mutations < 0 || operation.Mutations > lease.WorkBudget)
            return new(false, false, Reason: "cleanup-work-budget-exceeded");
        if (operation.RemovedPaths.Any(path => !lease.CleanupScope.Contains(path, StringComparer.Ordinal)))
            return new(false, false, Reason: "cleanup-path-outside-scope");
        recovery.CleanupOperations.Add(operation);
        recovery.NextAction = operation.Clean ? WorkflowTaskRecoveryActions.Verify : WorkflowTaskRecoveryActions.Inspect;
        await SaveRunAsync();
        return new(true, false, operation);
    }

    public async Task<WorkflowTaskSourceAdoptionResult> AuthorizeTaskSourceAdoptionAsync(
        WorkflowTaskSourceAdoptionRequest request)
    {
        RejectIfRunReloadRequired();
        if (!TryFindRecovery(request.Identity, request.BoundaryFingerprint, out _, out var task, out var recovery, out _))
            return new(false, false, Reason: "recovery-identity-mismatch");
        if (string.IsNullOrWhiteSpace(request.OperationId)
            || !request.Authenticated
            || !request.HasWorkflowPermission
            || string.IsNullOrWhiteSpace(request.OperatorId))
            return new(false, false, Reason: "recovery-operator-unauthorized");
        var existing = recovery.FindSourceAdoption(request.OperationId);
        if (existing is not null)
        {
            var same = existing.Identity == request.Identity
                && existing.SourcePaths.SequenceEqual(request.SourcePaths ?? [], StringComparer.Ordinal)
                && string.Equals(existing.Fence, request.Fence, StringComparison.Ordinal);
            return same ? new(true, true, existing) : new(false, false, Reason: "adoption-operation-conflict");
        }
        if (!HasCurrentRecoveryFence(recovery, request.Identity, request.BoundaryFingerprint, request.Fence))
            return new(false, false, Reason: "cleanup-fence-stale");
        var paths = NormalizePaths(request.SourcePaths);
        if (paths is null || paths.Count == 0)
            return new(false, false, Reason: "source-path-allowlist-invalid");
        var declaredArtifactPaths = task.Artifacts?.Files.Select(file => file.Path).ToArray() ?? [];
        var protectedPaths = (request.ProtectedPaths ?? [])
            .Concat(declaredArtifactPaths)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (protectedPaths.Any(path => !WorkflowRecoveryPathRules.IsSafeRelativePath(path))
            || paths.Any(path => (recovery.CleanupScope ?? []).Any(scope => WorkflowRecoveryPathRules.Overlaps(path, scope)))
            || paths.Any(path => protectedPaths.Any(protectedPath => WorkflowRecoveryPathRules.Overlaps(path, protectedPath))))
            return new(false, false, Reason: "source-path-overlaps-protected-scope");
        var adoption = new WorkflowTaskSourceAdoption(
            request.OperationId,
            request.Fence,
            request.Identity,
            request.OperatorId,
            paths,
            true,
            false,
            null,
            null,
            Now());
        recovery.SourceAdoptions.Add(adoption);
        recovery.NextAction = WorkflowTaskRecoveryActions.Verify;
        await SaveRunAsync();
        return new(true, false, adoption);
    }

    public async Task<WorkflowTaskSourceAdoptionResult> RecordTaskSourceAdoptionAsync(
        WorkflowTaskSourceAdoption operation)
    {
        RejectIfRunReloadRequired();
        if (!TryFindRecovery(operation.Identity, FindBoundaryFingerprint(operation.Identity), out _, out _, out var recovery, out _))
            return new(false, false, Reason: "recovery-identity-mismatch");
        var existing = recovery.FindSourceAdoption(operation.OperationId);
        if (existing is null || !existing.Accepted)
            return new(false, false, Reason: "adoption-not-authorized");
        if (!SameSourceAdoptionShape(existing, operation))
            return new(false, false, Reason: "adoption-operation-conflict");
        if (existing.Completed)
            return new(true, true, existing);
        if (!HasCurrentRecoveryFence(recovery, operation.Identity, FindBoundaryFingerprint(operation.Identity), operation.Fence))
            return new(false, false, Reason: "cleanup-fence-stale");
        var index = recovery.SourceAdoptions.IndexOf(existing);
        recovery.SourceAdoptions[index] = operation;
        recovery.NextAction = operation.Completed && operation.ResultingHead is not null
            ? WorkflowTaskRecoveryActions.Verify
            : WorkflowTaskRecoveryActions.AdoptTaskSourceChanges;
        await SaveRunAsync();
        return new(true, false, operation);
    }

    public async Task<WorkflowTaskFreshWorkspaceResult> AllocateFreshRecoveryWorkspaceAsync(
        WorkflowTaskExecutionIdentity identity,
        string boundaryFingerprint)
    {
        RejectIfRunReloadRequired();
        if (!TryFindRecovery(identity, boundaryFingerprint, out _, out _, out var recovery, out _))
            return new(false, null, null, null, "recovery-identity-mismatch");
        if (recovery.FreshWorkspaceId is not null
            && recovery.FreshWorkspaceGeneration is { } existingGeneration
            && recovery.FreshWorkspaceFence is not null)
            return new(true, recovery.FreshWorkspaceId, existingGeneration, recovery.FreshWorkspaceFence);
        var workspaceId = $"recovery-{Guid.NewGuid():N}";
        var generation = System.Text.Json.JsonSerializer.SerializeToElement($"generation-{Guid.NewGuid():N}");
        var fence = $"fresh-fence:{Guid.NewGuid():N}";
        recovery.FreshWorkspaceId = workspaceId;
        recovery.FreshWorkspaceGeneration = generation;
        recovery.FreshWorkspaceFence = fence;
        recovery.CurrentCleanupLease = null;
        recovery.NextAction = WorkflowTaskRecoveryActions.Inspect;
        await SaveRunAsync();
        return new(true, workspaceId, generation, fence);
    }

    private bool TryFindRecovery(
        WorkflowTaskExecutionIdentity identity,
        string boundaryFingerprint,
        out StageRun stage,
        out TaskRun task,
        out WorkflowTaskRecovery recovery,
        out WorkflowTaskCompletionBoundary boundary)
    {
        stage = null!;
        task = null!;
        recovery = null!;
        boundary = null!;
        if (_run is null) return false;
        var found = _run.Stages
            .SelectMany(candidate => candidate.Tasks.Select(item => (Stage: candidate, Task: item)))
            .SingleOrDefault(candidate => candidate.Task.CompletionBoundary is not null
                && candidate.Task.WorkflowTaskRecovery is not null
                && string.Equals(candidate.Task.Id, identity.TaskAttemptId, StringComparison.Ordinal));
        if (found.Task is null || found.Task.CompletionBoundary is null || found.Task.WorkflowTaskRecovery is null)
            return false;
        if (!WorkflowTaskCompletionBoundaryRules.SameIdentity(found.Task.CompletionBoundary.Identity, identity)
            || !string.Equals(found.Task.CompletionBoundary.Fingerprint, boundaryFingerprint, StringComparison.Ordinal))
            return false;
        stage = found.Stage;
        task = found.Task;
        recovery = found.Task.WorkflowTaskRecovery;
        boundary = found.Task.CompletionBoundary;
        return found.Task.Status == TaskRunStatus.Running;
    }

    private bool HasCurrentRecoveryFence(
        WorkflowTaskRecovery recovery,
        WorkflowTaskExecutionIdentity identity,
        string boundaryFingerprint,
        string? fence) =>
        !string.IsNullOrWhiteSpace(fence)
        && recovery.CurrentCleanupLease is { } lease
        && lease.ExpiresAt > Now()
        && string.Equals(lease.Fence, fence, StringComparison.Ordinal)
        && string.Equals(lease.BoundaryFingerprint, boundaryFingerprint, StringComparison.Ordinal)
        && WorkflowTaskCompletionBoundaryRules.SameIdentity(lease.Identity, identity);

    private string FindBoundaryFingerprint(WorkflowTaskExecutionIdentity identity) =>
        _run?.Stages.SelectMany(stage => stage.Tasks)
            .SingleOrDefault(task => string.Equals(task.Id, identity.TaskAttemptId, StringComparison.Ordinal))?
            .CompletionBoundary?.Fingerprint ?? string.Empty;

    private static List<string>? NormalizePaths(IReadOnlyList<string>? paths)
    {
        if (paths is null) return [];
        var normalized = paths.Select(path => path.Trim().Replace('\\', '/')).ToArray();
        if (normalized.Any(path => !WorkflowRecoveryPathRules.IsSafeRelativePath(path))) return null;
        return normalized.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static bool SameCleanupOperation(WorkflowTaskCleanupOperation left, WorkflowTaskCleanupOperation right) =>
        left.OperationId == right.OperationId
        && left.Fence == right.Fence
        && WorkflowTaskCompletionBoundaryRules.SameIdentity(left.Identity, right.Identity)
        && left.Applied == right.Applied
        && left.Clean == right.Clean
        && left.Mutations == right.Mutations
        && left.RemovedPaths.SequenceEqual(right.RemovedPaths, StringComparer.Ordinal)
        && left.Reason == right.Reason
        && left.RecordedAt == right.RecordedAt;

    private static bool SameSourceAdoptionShape(WorkflowTaskSourceAdoption left, WorkflowTaskSourceAdoption right) =>
        left.OperationId == right.OperationId
        && left.Fence == right.Fence
        && WorkflowTaskCompletionBoundaryRules.SameIdentity(left.Identity, right.Identity)
        && left.OperatorId == right.OperatorId
        && left.SourcePaths.SequenceEqual(right.SourcePaths, StringComparer.Ordinal);

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
