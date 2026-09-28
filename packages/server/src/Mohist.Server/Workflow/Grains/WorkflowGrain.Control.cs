using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Grains;

public partial class WorkflowGrain
{
    /// <summary>
    /// Atomically arbitrates a GitHub close against the workflow's Integrate
    /// boundary. Orleans serializes this command with approvals and task
    /// reports, so the boundary decision and stop cannot be separated by a
    /// stale read from the caller.
    /// </summary>
    public async Task<WorkflowWithdrawalResult> WithdrawIfBeforeIntegrateAsync(string? reason = null)
    {
        EnsureRun();
        if (!IsBeforeIntegrate(_run))
        {
            _log.LogDebug("Workflow {Id} ignored withdrawal at or past Integrate", GrainKey);
            return new WorkflowWithdrawalResult(WorkflowWithdrawalDisposition.Echo, "integrate_boundary_reached");
        }

        // A prior stop is already the requested withdrawal. Returning Applied
        // lets the Issue command converge if the original cross-aggregate
        // cancellation was interrupted after the workflow commit.
        if (_run.Status == WorkflowRunStatus.Stopped)
            return new WorkflowWithdrawalResult(WorkflowWithdrawalDisposition.Applied, "already_stopped");

        await StopAsync(reason ?? "github-close");
        return new WorkflowWithdrawalResult(WorkflowWithdrawalDisposition.Applied);
    }

    private static bool IsBeforeIntegrate(WorkflowRun run)
    {
        if (run.Status == WorkflowRunStatus.Completed)
            return false;

        var integrateIndex = run.Stages.FindIndex(stage =>
            string.Equals(stage.Id, "integrate", StringComparison.OrdinalIgnoreCase));
        var currentIndex = run.CurrentStageId is null
            ? -1
            : run.Stages.FindIndex(stage =>
                string.Equals(stage.Id, run.CurrentStageId, StringComparison.OrdinalIgnoreCase));
        return integrateIndex >= 0 && currentIndex >= 0 && currentIndex < integrateIndex;
    }

    public async Task ApproveAsync(
        string? decidedBy = null,
        string? displayName = null,
        WorkflowProvenanceActor? actor = null,
        string? source = null)
    {
        EnsureRun();
        var normalizedOperator = ApprovalOperatorValidation.Normalize(decidedBy);
        var normalizedDisplayName = ApprovalOperatorValidation.Normalize(displayName);
        var stage = _run.CurrentStage();
        var events = _run.Approve(Now(), normalizedOperator, normalizedDisplayName);
        events = events.Concat([
            ControlFact(
                WorkflowProvenanceActions.ManualApproval,
                "approved",
                stage.Id,
                stage.Attempt,
                actor,
                source,
                actorIdFallback: normalizedOperator,
                actorKindFallback: WorkflowProvenanceActorKinds.User)
        ]).ToArray();
        _log.LogInformation("Workflow {Id} approved at stage={Stage} by {Operator}", GrainKey, _run.CurrentStageId, normalizedOperator);
        await CommitAsync(events);
    }

    public async Task<string> RequestChangesAsync(
        string body,
        string? decidedBy = null,
        string? displayName = null,
        WorkflowProvenanceActor? actor = null,
        string? source = null)
    {
        EnsureRun();
        var normalizedOperator = ApprovalOperatorValidation.Normalize(decidedBy);
        var normalizedDisplayName = ApprovalOperatorValidation.Normalize(displayName);
        var stage = _run.CurrentStage();
        var definition = ResolveBoundDefinition(_run);
        var feedbackTasks = definition.Approval?.Feedback?.Tasks;
        var feedbackId = CreateFeedbackId();
        var events = _run.RequestChanges(body, feedbackId, Now(), normalizedOperator, feedbackTasks, normalizedDisplayName);
        events = events.Concat([
            ControlFact(
                WorkflowProvenanceActions.ManualApproval,
                "changes-requested",
                stage.Id,
                stage.Attempt,
                actor,
                source,
                result: feedbackId,
                actorIdFallback: normalizedOperator,
                actorKindFallback: WorkflowProvenanceActorKinds.User)
        ]).ToArray();
        _log.LogInformation("Workflow {Id} requested changes at stage={Stage} by {Operator}: feedback={FeedbackId}", GrainKey, stage.Id, normalizedOperator, feedbackId);
        await CommitAsync(events);
        return feedbackId;
    }


    public async Task RetryAsync(WorkflowProvenanceActor? actor = null, string? source = null)
    {
        EnsureRun();
        var retriedStageId = _run.CurrentStageId;
        var retryTarget = _run.RetryTarget();
        await ReleaseCurrentStageLocksAsync("retried");
        var now = Now();
        var events = _run.Retry(now);
        var stage = _run.CurrentStage();
        events = events.Concat([
            ControlFact(
                WorkflowProvenanceActions.Retry,
                "resumed",
                stage.Id,
                stage.Attempt,
                actor,
                source,
                result: retryTarget?.Reason.ToString(),
                reason: retryTarget?.Reason.ToString(),
                target: retryTarget?.Target)
        ]).ToArray();
        _log.LogInformation("Workflow {Id} retry at stage={Stage}", GrainKey, _run.CurrentStageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            // The retry did not persist. Re-acquire the lock for the stage we
            // just released so the rolled-back run still holds its sequential
            // lock until a later successful transition releases it.
            if (retriedStageId is not null)
                await AcquireStageLocksIfNeededAsync(retriedStageId);
            throw;
        }
    }

    public async Task RerunAsync(WorkflowProvenanceActor? actor = null, string? source = null)
    {
        EnsureRun();
        var rerunStageId = _run.CurrentStageId;
        await ReleaseCurrentStageLocksAsync("rerun");
        var now = Now();
        var events = _run.Rerun(now);
        var stage = _run.CurrentStage();
        events = events.Concat([
            ControlFact(
                WorkflowProvenanceActions.Rerun,
                "resumed",
                stage.Id,
                stage.Attempt,
                actor,
                source,
                reason: "current-stage",
                target: stage.Id)
        ]).ToArray();
        _log.LogInformation("Workflow {Id} rerun at stage={Stage}", GrainKey, _run.CurrentStageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            if (rerunStageId is not null)
                await AcquireStageLocksIfNeededAsync(rerunStageId);
            throw;
        }
    }

    public async Task<WorkflowControlResult> RerunFromStageAsync(
        string stageId,
        WorkflowProvenanceActor? actor = null,
        string? source = null)
    {
        EnsureRun();
        IReadOnlyList<WorkflowEvent> events;
        try
        {
            events = _run.RerunFromStage(stageId, Now());
        }
        catch (WorkflowControlRejectionException ex)
        {
            return WorkflowControlResult.Rejected(ex.Code, ex.Message, ex.DetailsJson());
        }

        var targetIdx = _run.Stages.FindIndex(s => s.Id == stageId);
        var targetStage = _run.Stages[targetIdx];
        var releasedStages = new List<string>();
        for (var i = targetIdx; i < _run.Stages.Count; i++)
        {
            releasedStages.Add(_run.Stages[i].Id);
            await ReleaseStageLocksAsync(_run.Stages[i].Id, "rerun-from-stage");
        }
        events = events.Concat([
            ControlFact(
                WorkflowProvenanceActions.Rerun,
                "resumed",
                targetStage.Id,
                targetStage.Attempt,
                actor,
                source,
                reason: "from-stage",
                target: targetStage.Id)
        ]).ToArray();
        _log.LogInformation("Workflow {Id} rerun-from-stage at stage={Stage}", GrainKey, stageId);
        try
        {
            await CommitAsync(events);
        }
        catch
        {
            // Re-acquire the locks for stages we released; the rerun-from-stage
            // did not persist, so the rolled-back run still requires them.
            foreach (var released in releasedStages)
                await AcquireStageLocksIfNeededAsync(released);
            throw;
        }
        return WorkflowControlResult.Ok();
    }

    private WorkflowProvenanceRecorded ControlFact(
        string action,
        string outcome,
        string stage,
        int attempt,
        WorkflowProvenanceActor? actor,
        string? source,
        string? result = null,
        string? reason = null,
        string? target = null,
        string? actorIdFallback = null,
        string actorKindFallback = WorkflowProvenanceActorKinds.System)
    {
        var resolved = actor
            ?? new WorkflowProvenanceActor(
                actorKindFallback,
                actorIdFallback ?? "workflow",
                null);
        return new WorkflowProvenanceRecorded(
            action,
            resolved.Kind,
            resolved.Id,
            string.IsNullOrWhiteSpace(source) ? "workflow-control" : source,
            outcome,
            Stage: stage,
            Attempt: attempt,
            Result: result,
            Reason: reason,
            Target: target,
            ActorDisplayName: resolved.DisplayName);
    }

    private static string CreateFeedbackId() => $"fb_{Guid.NewGuid():N}";
}
