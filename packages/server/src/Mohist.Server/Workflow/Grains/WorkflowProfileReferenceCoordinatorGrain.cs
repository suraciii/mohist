using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Workflow.Grains.Coordinator;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Orleans;
using Orleans.Runtime;
using Mohist.Workflow.Definition;

namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Project-scoped, non-reentrant (Orleans default)
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
    private readonly WorkflowProfileDeletionBlockerQuery? _blockerQuery;
    private readonly ILogger<WorkflowProfileReferenceCoordinatorGrain> _log;

    public WorkflowProfileReferenceCoordinatorGrain(
        [PersistentState("workflow-profile-coordinator")] IPersistentState<WorkflowProfileCoordinatorState> state,
        IGrainFactory grains,
        ILogger<WorkflowProfileReferenceCoordinatorGrain> log,
        IWorkflowProfileProvider provider,
        WorkflowProfileDeletionBlockerQuery? blockerQuery = null)
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
            _ = await ReplayPendingAsync(pending);
        }
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

        var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(payload.ProjectId);
        var outcome = await participant.SetDefaultAsync(payload, commandId, pending.CapturedRevision);
        await ClearFenceAsync(commandId);
        return new WorkflowProfileReferenceResult(
            Code: MapProjectOutcome(outcome),
            ProfileId: payload.ProfileId,
            AppliedRevision: pending.CapturedRevision);
    }

    public async Task<WorkflowProfileReferenceResult> BindWorkflowRunAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        if (_state.State.Pending is { } existingFence)
        {
            if (string.Equals(existingFence.CommandId, commandId, StringComparison.Ordinal))
            {
                if (existingFence.Kind != WorkflowProfileCommandPayloadKinds.BindWorkflowRun)
                {
                    return new WorkflowProfileReferenceResult(
                        WorkflowProfileReferenceResultCode.ConflictingRequest,
                        existingFence.ProfileId,
                        existingFence.ExpectedRevision,
                        $"Command '{commandId}' was reused with a different canonical payload");
                }
                var pendingPayload = (WorkflowProfileCommandPayload.BindWorkflowRun)
                    WorkflowProfileCommandPayloadCodec.Deserialize(existingFence.Kind, existingFence.PayloadJson);
                if (!SameStartRequest(payload, pendingPayload))
                {
                    return new WorkflowProfileReferenceResult(
                        WorkflowProfileReferenceResultCode.ConflictingRequest,
                        pendingPayload.ProfileId,
                        existingFence.ExpectedRevision,
                        $"Command '{commandId}' was reused with different startup facts");
                }
            }
            _ = await ReplayPendingAsync(existingFence);
        }

        var participant = _grains.GetGrain<IWorkflowRunBindingParticipant>(payload.WorkflowRunId);
        var receipt = await participant.GetBindingAsync(payload);
        if (receipt.Outcome == WorkflowRunBindingOutcome.Conflict)
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.ConflictingRequest,
                receipt.Binding?.ProfileId ?? payload.ExplicitProfileId ?? string.Empty,
                expectedRevision ?? 0L,
                receipt.Message,
                Binding: receipt.Binding);
        if (receipt.Binding is not null)
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.AlreadyApplied,
                receipt.Binding.ProfileId,
                expectedRevision ?? 0L,
                Binding: receipt.Binding);

        var bound = await ResolveWorkflowStartAsync(payload);
        if (bound is null)
            return new WorkflowProfileReferenceResult(
                WorkflowProfileReferenceResultCode.ProfileUnknown,
                payload.ExplicitProfileId ?? string.Empty,
                expectedRevision ?? 0L,
                "No enabled Workflow Profile is available for this Project");

        var boundPayload = payload with { Bound = bound };
        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.BindWorkflowRun,
            bound.ProfileId,
            commandId,
            expectedRevision,
            boundPayload);

        if (pending.Replay is not null)
            return pending.Replay;

        var outcome = await participant.BindAsync(bound, commandId, pending.CapturedRevision);
        await ClearFenceAsync(commandId);
        return new WorkflowProfileReferenceResult(
            Code: MapRunOutcome(outcome.Outcome),
            ProfileId: bound.ProfileId,
            AppliedRevision: pending.CapturedRevision,
            Message: outcome.Message,
            Binding: outcome.Binding);
    }

    public async Task<WorkflowProfileReferenceResult> SetAgentActionOverrideAsync(
        WorkflowProfileCommandPayload.SetAgentActionOverride payload,
        string commandId,
        long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_state.State.Pending is { } existing)
        {
            var payloadJson = WorkflowProfileCommandPayloadCodec.Serialize(payload);
            if (string.Equals(existing.CommandId, commandId, StringComparison.Ordinal))
            {
                if (existing.Kind != payload.Kind
                    || !string.Equals(existing.PayloadJson, payloadJson, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Command '{commandId}' was reused with a different canonical payload");
                return await ReplayPendingAsync(existing)
                    ?? throw new InvalidOperationException(
                        $"Pending command '{commandId}' did not produce an Agent Action override result");
            }
            _ = await ReplayPendingAsync(existing);
        }
        await _provider.ValidateAgentActionOverrideAsync(
            payload.ProjectId,
            payload.ProfileId,
            payload.AgentAction);

        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.SetAgentActionOverride,
            payload.ProfileId,
            commandId,
            expectedRevision,
            payload);
        var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(payload.ProjectId);
        var outcome = await participant.SetAgentActionOverrideAsync(payload, commandId, pending.CapturedRevision);
        await ClearFenceAsync(commandId);
        return new WorkflowProfileReferenceResult(
            MapProjectOutcome(outcome),
            payload.ProfileId,
            pending.CapturedRevision);
    }

    public async Task<WorkflowProfileSaveResult> UpdateProfileAsync(
        WorkflowProfileCommandPayload.UpdateProfile payload,
        string commandId,
        long? expectedRevision)
    {
        var pending = await AcquireFenceAsync(
            WorkflowProfileCommandPayloadKinds.UpdateProfile,
            payload.ProfileId,
            commandId,
            expectedRevision,
            payload);
        try
        {
            var result = await _provider.UpdateAsync(
                payload.ProjectId,
                new WorkflowProfileCollectionEntry(
                    payload.ProjectId,
                    payload.ProfileId,
                    payload.Name,
                    payload.Description,
                    WorkflowProfileSourceProvenance.Verbatim,
                    IsBuiltIn: false,
                    payload.DefinitionSource));
            await ClearFenceAsync(commandId);
            return result;
        }
        catch (WorkflowDefinitionValidationException)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
        catch (WorkflowProfileNotFoundException)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
        catch (WorkflowProfileReadOnlyException)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
        catch (ArgumentException)
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

        var blockerQuery = _blockerQuery
            ?? throw new InvalidOperationException("WorkflowProfileDeletionBlockerQuery is unavailable");
        var blockers = await blockerQuery.GetBlockersAsync(payload.ProjectId, payload.ProfileId);
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
            var currentBlockers = await blockerQuery.GetBlockersAsync(
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
            var payloadJson = WorkflowProfileCommandPayloadCodec.Serialize(payload);
            if (string.Equals(existing.CommandId, commandId, StringComparison.Ordinal))
            {
                if (existing.Kind == kind
                    && string.Equals(existing.ProfileId, profileId, StringComparison.Ordinal)
                    && string.Equals(existing.PayloadJson, payloadJson, StringComparison.Ordinal))
                {
                    return new FenceDecision(existing.ExpectedRevision, null);
                }
                throw new InvalidOperationException(
                    $"Command '{commandId}' was reused with a different canonical payload");
            }

            _ = await ReplayPendingAsync(existing);
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

    private async Task<WorkflowProfileReferenceResult?> ReplayPendingAsync(PendingWorkflowProfileCommand pending)
    {
        var payload = WorkflowProfileCommandPayloadCodec.Deserialize(pending.Kind, pending.PayloadJson);
        WorkflowProfileReferenceResult? result = null;
        try
        {
            switch (pending.Kind)
            {
                case WorkflowProfileCommandPayloadKinds.SetProjectDefault:
                {
                    var p = (WorkflowProfileCommandPayload.SetProjectDefault)payload;
                    var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(p.ProjectId);
                    var outcome = await participant.SetDefaultAsync(p, pending.CommandId, pending.ExpectedRevision);
                    result = new WorkflowProfileReferenceResult(
                        MapProjectOutcome(outcome), p.ProfileId, pending.ExpectedRevision);
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.BindWorkflowRun:
                {
                    var p = (WorkflowProfileCommandPayload.BindWorkflowRun)payload;
                    if (p.Bound is null)
                        throw new InvalidOperationException("Pending WorkflowRun binding has no resolved startup payload");
                    var participant = _grains.GetGrain<IWorkflowRunBindingParticipant>(p.WorkflowRunId);
                    var outcome = await participant.BindAsync(p.Bound, pending.CommandId, pending.ExpectedRevision);
                    result = new WorkflowProfileReferenceResult(
                        MapRunOutcome(outcome.Outcome),
                        outcome.Binding?.ProfileId ?? p.Bound.ProfileId,
                        pending.ExpectedRevision,
                        outcome.Message,
                        Binding: outcome.Binding);
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.SetAgentActionOverride:
                {
                    var p = (WorkflowProfileCommandPayload.SetAgentActionOverride)payload;
                    var participant = _grains.GetGrain<IProjectWorkflowProfileBindingParticipant>(p.ProjectId);
                    var outcome = await participant.SetAgentActionOverrideAsync(p, pending.CommandId, pending.ExpectedRevision);
                    result = new WorkflowProfileReferenceResult(
                        MapProjectOutcome(outcome), p.ProfileId, pending.ExpectedRevision);
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.UpdateProfile:
                {
                    var p = (WorkflowProfileCommandPayload.UpdateProfile)payload;
                    await _provider.UpdateAsync(
                        p.ProjectId,
                        new WorkflowProfileCollectionEntry(
                            p.ProjectId,
                            p.ProfileId,
                            p.Name,
                            p.Description,
                            WorkflowProfileSourceProvenance.Verbatim,
                            IsBuiltIn: false,
                            p.DefinitionSource));
                    break;
                }
                case WorkflowProfileCommandPayloadKinds.DeleteProfile:
                {
                    var p = (WorkflowProfileCommandPayload.DeleteProfile)payload;
                    var deleted = await _provider.DeleteAsync(p.ProjectId, p.ProfileId);
                    result = new WorkflowProfileReferenceResult(
                        deleted
                            ? WorkflowProfileReferenceResultCode.Applied
                            : WorkflowProfileReferenceResultCode.ProfileUnknown,
                        p.ProfileId,
                        pending.ExpectedRevision);
                    break;
                }
            }
        }
        catch (DbUpdateException) when (pending.Kind == WorkflowProfileCommandPayloadKinds.DeleteProfile)
        {
            // The FK rejection is a definitive result: another aggregate now
            // owns a reference, so the original delete command is complete.
        }
        catch (WorkflowProfileReadOnlyException) when (pending.Kind == WorkflowProfileCommandPayloadKinds.DeleteProfile)
        {
            // Built-in Profiles are permanently non-deletable.
        }
        catch (Exception ex)
        {
            _log.LogInformation(
                "WorkflowProfileReferenceCoordinator {ProjectId} replay of command {CommandId} ({Kind}) terminated with {Exception}",
                ProjectId, pending.CommandId, pending.Kind, ex.GetType().Name);
            throw;
        }

        _state.State = _state.State with { Pending = null };
        await _state.WriteStateAsync();
        return result;
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
            WorkflowRunBindingOutcome.Conflict => WorkflowProfileReferenceResultCode.ConflictingRequest,
            _ => WorkflowProfileReferenceResultCode.Applied,
        };

    private async Task<BoundWorkflowStart?> ResolveWorkflowStartAsync(
        WorkflowProfileCommandPayload.BindWorkflowRun request)
    {
        var disabled = await _provider.GetDisabledProfileIdsAsync(request.ProjectId);
        var projectDefault = await _provider.GetDefaultProfileIdAsync(request.ProjectId);
        var selected = await ResolveEffectiveProfileIdAsync(
            request.ProjectId,
            request.ExplicitProfileId,
            projectDefault,
            disabled);
        if (selected is null)
            return null;

        var entry = await _provider.GetAsync(request.ProjectId, selected);
        if (entry is null) return null;
        var definition = await _provider.GetDefinitionAsync(
            request.ProjectId,
            selected,
            entry.AgentAction);
        if (definition is null || definition.Stages.Count == 0)
            return null;
        var metadata = request.Metadata with
        {
            ProjectId = request.ProjectId,
            IssueNumber = request.IssueNumber,
            EpicNumber = request.EpicNumber,
        };
        return new BoundWorkflowStart(
            request.WorkflowRunId,
            request.ProjectId,
            request.IssueNumber,
            request.EpicNumber,
            request.ExplicitProfileId,
            selected,
            entry.AgentAction,
            definition.Stages.Select(stage => new BoundStageStructure(stage.Stage, stage.RequiresApproval)).ToList(),
            metadata,
            request.Workspace,
            DefinitionJson: WorkflowYamlSerializer.ToJson(definition));
    }

    private async Task<string?> ResolveEffectiveProfileIdAsync(
        string projectId,
        string? issueSelection,
        string? projectDefault,
        IReadOnlySet<string> disabledIds)
    {
        foreach (var profileId in CandidateProfileIds(issueSelection, projectDefault, disabledIds))
        {
            if (await _provider.ContainsAsync(projectId, profileId))
                return profileId;
        }

        return null;
    }

    private static IEnumerable<string> CandidateProfileIds(
        string? issueSelection,
        string? projectDefault,
        IReadOnlySet<string> disabledIds)
    {
        if (!string.IsNullOrWhiteSpace(issueSelection)
            && !IsDisabledSystemProfile(issueSelection, disabledIds))
        {
            yield return issueSelection;
        }

        if (!string.IsNullOrWhiteSpace(projectDefault)
            && !string.Equals(projectDefault, issueSelection, StringComparison.Ordinal)
            && !IsDisabledSystemProfile(projectDefault, disabledIds))
        {
            yield return projectDefault;
        }

        foreach (var systemProfileId in WorkflowProfileCatalog.SystemProfileIds)
        {
            if (string.Equals(systemProfileId, issueSelection, StringComparison.Ordinal)
                || string.Equals(systemProfileId, projectDefault, StringComparison.Ordinal)
                || IsDisabledSystemProfile(systemProfileId, disabledIds))
            {
                continue;
            }

            yield return systemProfileId;
        }
    }

    private static bool IsDisabledSystemProfile(string profileId, IReadOnlySet<string> disabledIds) =>
        WorkflowProfileCatalog.IsSystemProfile(profileId) && disabledIds.Contains(profileId);

    internal static bool SameStartRequest(
        WorkflowProfileCommandPayload.BindWorkflowRun left,
        WorkflowProfileCommandPayload.BindWorkflowRun right) =>
        string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && string.Equals(left.WorkflowRunId, right.WorkflowRunId, StringComparison.Ordinal)
        && left.IssueNumber == right.IssueNumber
        && left.EpicNumber == right.EpicNumber
        && string.Equals(left.ExplicitProfileId, right.ExplicitProfileId, StringComparison.Ordinal)
        && SameMetadataIdentity(left.Metadata, right.Metadata)
        && Equals(left.Workspace, right.Workspace);

    private static bool SameMetadataIdentity(WorkflowRunMetadata left, WorkflowRunMetadata right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.ProjectId, right.ProjectId, StringComparison.Ordinal)
        && left.IssueNumber == right.IssueNumber
        && left.EpicNumber == right.EpicNumber
        && SameDictionary(left.Labels, right.Labels)
        && SameDictionary(left.Annotations, right.Annotations);

    private static bool SameDictionary(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        return left.All(pair => right.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static WorkflowProfileDeletionBlockersDto ToDto(WorkflowProfileDeletionBlockers blockers) =>
        new(
            blockers.ProjectDefault,
            blockers.IssueSelections
                .Select(i => new WorkflowProfileIssueBlockerDto(i.ProjectId, i.IssueNumber, i.Status))
                .ToList(),
            blockers.ActiveRuns
                .Select(run => new WorkflowProfileRunBlockerDto(run.WorkflowRunId, run.Status))
                .ToList());

    private static string FormatBlockersMessage(string profileId, WorkflowProfileDeletionBlockers blockers)
    {
        var parts = new List<string>();
        if (blockers.ProjectDefault)
            parts.Add("Project default reference");
        foreach (var issue in blockers.IssueSelections)
            parts.Add($"Issue #{issue.IssueNumber} ({issue.Status}) selection");
        foreach (var run in blockers.ActiveRuns)
            parts.Add($"active WorkflowRun '{run.WorkflowRunId}' ({run.Status}) binding");
        return $"WorkflowProfile '{profileId}' is still referenced: {string.Join("; ", parts)}";
    }

    private readonly record struct FenceDecision(
        long CapturedRevision,
        WorkflowProfileReferenceResult? Replay);
}
