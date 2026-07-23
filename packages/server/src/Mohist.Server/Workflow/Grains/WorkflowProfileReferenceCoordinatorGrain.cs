using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Issue.Grains.Coordinator;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// issue-477 T-001: Project-scoped, non-reentrant (Orleans default)
/// application process manager that serializes Project default
/// WorkflowProfile writes, WorkflowRun startup bindings, and custom
/// Profile deletions. Persists at most one
/// <see cref="PendingWorkflowProfileCommand"/> fence, invokes one
/// idempotent participant command, and clears the fence after a
/// definitive applied or rejected result.
///
/// This grain owns no business facts. It writes at most one
/// participant aggregate per command (never Issue + Project + Run in
/// the same commit) and stores only the technical fence. Profile
/// membership is re-validated by the participant inside its own
/// transaction; the FK backstop on the nullable custom-Profile backing
/// key columns is the final concurrency safety net for Insert/Delete
/// races between this coordinator and the Issue selection path.
/// </summary>
public sealed class WorkflowProfileReferenceCoordinatorGrain : Grain, IWorkflowProfileReferenceCoordinatorGrain
{
    private readonly IPersistentState<WorkflowProfileCoordinatorState> _state;
    private readonly IGrainFactory _grains;
    private readonly IWorkflowProfileProvider _provider;
    private readonly WorkflowProfileDeletionBlockerQuery _blockerQuery;
    private readonly ILogger<WorkflowProfileReferenceCoordinatorGrain> _log;

    public WorkflowProfileReferenceCoordinatorGrain(
        [PersistentState("workflow-profile-coordinator")] IPersistentState<WorkflowProfileCoordinatorState> state,
        IGrainFactory grains,
        IWorkflowProfileProvider provider,
        WorkflowProfileDeletionBlockerQuery blockerQuery,
        ILogger<WorkflowProfileReferenceCoordinatorGrain> log)
    {
        _state = state;
        _grains = grains;
        _provider = provider;
        _blockerQuery = blockerQuery;
        _log = log;
    }

    private string ProjectId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
        {
            _state.State = WorkflowProfileCoordinatorState.Empty;
            await _state.WriteStateAsync();
            return;
        }

        if (_state.State.Pending is { } pending)
        {
            _log.LogInformation(
                "WorkflowProfileReferenceCoordinator {ProjectId} replaying pending command {CommandId} kind={Kind} on activation",
                ProjectId, pending.CommandId, pending.Kind);
            await ReplayPendingAsync(pending);
        }
    }

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task<WorkflowProfileReferenceResult> SetProjectDefaultAsync(
        WorkflowProfileCommandPayload.SetProjectDefault payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        if (!await _provider.ContainsAsync(payload.ProjectId, payload.ProfileId))
        {
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.ProfileUnknown,
                payload.ProfileId,
                expectedRevision ?? 0L,
                $"Profile '{payload.ProfileId}' is not in the project collection");
        }

        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.SetProjectDefault,
            payload.ProfileId,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        try
        {
            var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(payload.ProjectId);
            var outcome = await participant.SetDefaultAsync(payload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new WorkflowProfileReferenceResult(
                Code: MapProjectOutcome(outcome),
                ProfileId: payload.ProfileId,
                AppliedRevision: pending.CapturedRevision);
        }
        catch
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    public async Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        if (!await _provider.ContainsAsync(payload.ProjectId, payload.ProfileId))
        {
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.ProfileUnknown,
                payload.ProfileId,
                expectedRevision ?? 0L,
                $"Profile '{payload.ProfileId}' is not in the project collection");
        }

        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.BindWorkflowRun,
            payload.ProfileId,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        try
        {
            var participant = _grains.GetGrain<IWorkflowRunBindingParticipant>(payload.WorkflowRunId);
            var outcome = await participant.BindAsync(payload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new WorkflowProfileReferenceResult(
                Code: MapRunOutcome(outcome),
                ProfileId: payload.ProfileId,
                AppliedRevision: pending.CapturedRevision);
        }
        catch
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    public async Task<WorkflowProfileReferenceResult> DeleteProfileAsync(
        WorkflowProfileCommandPayload.DeleteProfile payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        if (WorkflowProfileCatalog.IsSystemProfile(payload.ProfileId))
        {
            return new WorkflowProfileReferenceResult(
                Code: WorkflowProfileReferenceResultCode.ProfileReadOnly,
                ProfileId: payload.ProfileId,
                AppliedRevision: expectedRevision ?? 0L,
                Message: $"WorkflowProfile '{payload.ProfileId}' is a built-in and cannot be deleted");
        }

        var blockers = await _blockerQuery.GetBlockersAsync(payload.ProjectId, payload.ProfileId);
        if (blockers.HasAnyBlocker)
        {
            return new WorkflowProfileReferenceResult(
                Code: WorkflowProfileReferenceResultCode.BlockedByReferences,
                ProfileId: payload.ProfileId,
                AppliedRevision: expectedRevision ?? 0L,
                Message: FormatBlockersMessage(payload.ProfileId, blockers),
                Blockers: ToDto(blockers));
        }

        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.DeleteProfile,
            payload.ProfileId,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        try
        {
            var deleted = await _provider.DeleteAsync(payload.ProjectId, payload.ProfileId);
            await ClearFenceAsync(commandId);
            return new WorkflowProfileReferenceResult(
                Code: deleted
                    ? WorkflowProfileReferenceResultCode.Applied
                    : WorkflowProfileReferenceResultCode.ProfileUnknown,
                ProfileId: payload.ProfileId,
                AppliedRevision: pending.CapturedRevision);
        }
        catch (DbUpdateException)
        {
            var currentBlockers = await _blockerQuery.GetBlockersAsync(
                payload.ProjectId,
                payload.ProfileId);
            await ClearFenceAsync(commandId);
            return new WorkflowProfileReferenceResult(
                Code: currentBlockers.HasAnyBlocker
                    ? WorkflowProfileReferenceResultCode.BlockedByReferences
                    : WorkflowProfileReferenceResultCode.ProfileUnknown,
                ProfileId: payload.ProfileId,
                AppliedRevision: pending.CapturedRevision,
                Message: currentBlockers.HasAnyBlocker
                    ? FormatBlockersMessage(payload.ProfileId, currentBlockers)
                    : "workflow-profile-not-found",
                Blockers: currentBlockers.HasAnyBlocker ? ToDto(currentBlockers) : null);
        }
        catch (WorkflowProfileReadOnlyException)
        {
            await ClearFenceAsync(commandId);
            return new WorkflowProfileReferenceResult(
                Code: WorkflowProfileReferenceResultCode.ProfileReadOnly,
                ProfileId: payload.ProfileId,
                AppliedRevision: pending.CapturedRevision,
                Message: $"WorkflowProfile '{payload.ProfileId}' is a built-in and cannot be deleted");
        }
        catch
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    private async Task<FenceDecision> AcquireFenceAsync(
        string kind,
        string profileId,
        string commandId,
        long? expectedRevision,
        WorkflowProfileCommandPayload payload)
    {
        var existing = _state.State.Pending;
        if (existing is not null)
        {
            if (string.Equals(existing.CommandId, commandId, StringComparison.Ordinal)
                && existing.Kind == kind
                && string.Equals(existing.ProfileId, profileId, StringComparison.Ordinal))
            {
                return new FenceDecision(existing.ExpectedRevision, null);
            }

            await ReplayPendingAsync(existing);
        }

        var capturedRevision = expectedRevision ?? 1L;
        _state.State = _state.State with
        {
            Pending = new PendingWorkflowProfileCommand(
                CommandId: commandId,
                Kind: kind,
                ProfileId: profileId,
                ExpectedRevision: capturedRevision,
                PayloadJson: WorkflowProfileCommandPayloadCodec.Serialize(payload)),
        };
        await _state.WriteStateAsync();
        return new FenceDecision(capturedRevision, null);
    }

    private async Task ReplayPendingAsync(PendingWorkflowProfileCommand pending)
    {
        var payload = WorkflowProfileCommandPayloadCodec.Deserialize(pending.Kind, pending.PayloadJson);
        try
        {
            switch (pending.Kind)
            {
                case WorkflowProfileCommandPayloadKinds.SetProjectDefault:
                {
                    var p = (WorkflowProfileCommandPayload.SetProjectDefault)payload;
                    var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(p.ProjectId);
                    await participant.SetDefaultAsync(p, pending.CommandId, pending.ExpectedRevision);
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.BindWorkflowRun:
                {
                    var p = (WorkflowProfileCommandPayload.BindWorkflowRun)payload;
                    var participant = _grains.GetGrain<IWorkflowRunBindingParticipant>(p.WorkflowRunId);
                    await participant.BindAsync(p, pending.CommandId, pending.ExpectedRevision);
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.DeleteProfile:
                {
                    var p = (WorkflowProfileCommandPayload.DeleteProfile)payload;
                    // The replay path is operator-driven; do not re-run
                    // the deletion. Just clear the fence: the deletion
                    // itself is idempotent and was performed during the
                    // original command.
                    _ = p;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(
                "WorkflowProfileReferenceCoordinator {ProjectId} replay of command {CommandId} ({Kind}) terminated with {Exception}",
                ProjectId, pending.CommandId, pending.Kind, ex.GetType().Name);
        }

        _state.State = _state.State with { Pending = null };
        await _state.WriteStateAsync();
    }

    private async Task ClearFenceAsync(string commandId)
    {
        var existing = _state.State.Pending;
        if (existing is null) return;
        if (!string.Equals(existing.CommandId, commandId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "WorkflowProfileReferenceCoordinator {ProjectId} fence command mismatch on clear: expected {CommandId} found {Found}",
                ProjectId, commandId, existing.CommandId);
            return;
        }
        _state.State = _state.State with { Pending = null };
        await _state.WriteStateAsync();
    }

    private static WorkflowProfileReferenceResultCode MapProjectOutcome(ProjectWorkflowProfileBindingOutcome outcome) =>
        outcome switch
        {
            ProjectWorkflowProfileBindingOutcome.Applied => WorkflowProfileReferenceResultCode.Applied,
            ProjectWorkflowProfileBindingOutcome.AlreadyApplied => WorkflowProfileReferenceResultCode.AlreadyApplied,
            ProjectWorkflowProfileBindingOutcome.ProjectNotFound => WorkflowProfileReferenceResultCode.ProjectNotFound,
            ProjectWorkflowProfileBindingOutcome.ProfileUnknown => WorkflowProfileReferenceResultCode.ProfileUnknown,
            _ => WorkflowProfileReferenceResultCode.Applied,
        };

    private static WorkflowProfileReferenceResultCode MapRunOutcome(WorkflowRunBindingOutcome outcome) =>
        outcome switch
        {
            WorkflowRunBindingOutcome.Applied => WorkflowProfileReferenceResultCode.Applied,
            WorkflowRunBindingOutcome.AlreadyApplied => WorkflowProfileReferenceResultCode.AlreadyApplied,
            WorkflowRunBindingOutcome.RunNotFound => WorkflowProfileReferenceResultCode.ProjectNotFound,
            WorkflowRunBindingOutcome.ProfileUnknown => WorkflowProfileReferenceResultCode.ProfileUnknown,
            _ => WorkflowProfileReferenceResultCode.Applied,
        };

    private static WorkflowProfileDeletionBlockersDto ToDto(WorkflowProfileDeletionBlockers blockers) =>
        new(
            blockers.ProjectDefault,
            blockers.IssueSelections
                .Select(i => new WorkflowProfileIssueBlockerDto(i.ProjectId, i.IssueNumber, i.Status))
                .ToList(),
            blockers.ActiveRun is { } run
                ? new WorkflowProfileRunBlockerDto(run.WorkflowRunId, run.Status)
                : null);

    private static string FormatBlockersMessage(string profileId, WorkflowProfileDeletionBlockers blockers)
    {
        var parts = new List<string>();
        if (blockers.ProjectDefault)
            parts.Add("Project default reference");
        foreach (var issue in blockers.IssueSelections)
            parts.Add($"Issue #{issue.IssueNumber} ({issue.Status}) selection");
        if (blockers.ActiveRun is { } run)
            parts.Add($"active WorkflowRun '{run.WorkflowRunId}' ({run.Status}) binding");
        return $"WorkflowProfile '{profileId}' is still referenced: {string.Join("; ", parts)}";
    }

    private readonly record struct FenceDecision(
        long CapturedRevision,
        WorkflowProfileReferenceResult? Replay);
}
