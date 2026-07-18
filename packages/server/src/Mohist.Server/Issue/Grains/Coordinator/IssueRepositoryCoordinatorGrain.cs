using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Grains;
using Mohist.Server.Project.Services;
using Orleans;
using Orleans.Runtime;

namespace Mohist.Server.Issue.Grains.Coordinator;

/// <summary>
/// issue-417 T-005: Project-scoped, non-reentrant (Orleans default)
/// application process manager that serializes Issue create, target
/// reassignment, cancelled-Issue reopen, and Project repository
/// removal. Persists at most one <see cref="PendingRepositoryCommand"/>
/// fence, invokes one idempotent participant command, and clears the
/// fence after a definitive applied or rejected result.
/// <para>
/// This grain owns no business facts. It writes at most one
/// participant aggregate per command (never both Issue and Project in
/// the same commit) and stores only the technical fence. A lost
/// response, activation deactivation, or unknown downstream result
/// leaves the fence in place; the next call replays the surviving
/// command before accepting a new one.
/// </para>
/// </summary>
public class IssueRepositoryCoordinatorGrain : Grain, IIssueRepositoryCoordinatorGrain
{
    private readonly IPersistentState<RepositoryCoordinatorState> _state;
    private readonly IGrainFactory _grains;
    private readonly RepositoryDeletionBlockerQuery _blockerQuery;
    private readonly ILogger<IssueRepositoryCoordinatorGrain> _log;

    public IssueRepositoryCoordinatorGrain(
        [PersistentState("coordinator")] IPersistentState<RepositoryCoordinatorState> state,
        IGrainFactory grains,
        RepositoryDeletionBlockerQuery blockerQuery,
        ILogger<IssueRepositoryCoordinatorGrain> log)
    {
        _state = state;
        _grains = grains;
        _blockerQuery = blockerQuery;
        _log = log;
    }

    private string ProjectId => this.GetPrimaryKeyString();

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!_state.RecordExists)
        {
            _state.State = RepositoryCoordinatorState.Empty;
            await _state.WriteStateAsync();
            return;
        }

        if (_state.State.Pending is { } pending)
        {
            _log.LogInformation(
                "IssueRepositoryCoordinator {ProjectId} replaying pending command {CommandId} kind={Kind} on activation",
                ProjectId, pending.CommandId, pending.Kind);
            await ReplayPendingAsync(pending);
        }
    }

    public Task DeactivateForTestAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public async Task<IssueRepositoryBindingResult> CreateIssueAsync(
        RepositoryCommandPayload.Create payload, string commandId, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        var pending = await AcquireFenceAsync(
            RepositoryCommandPayloadKinds.Create,
            payload.RepositoryName,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        await CoordinatorProbe.AfterFencePersistedAsync(
            CoordinatorProbeKind.Create, ProjectId, commandId);

        try
        {
            var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(payload.ProjectId, payload.IssueNumber));
            var outcome = await participant.CreateAsync(payload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                Code: MapApplied(outcome),
                RepositoryName: payload.RepositoryName,
                AppliedRevision: pending.CapturedRevision);
        }
        catch (IssueRepositoryUnknownException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryUnknown,
                payload.RepositoryName,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (IssueRepositoryStaleRevisionException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryStaleRevision,
                payload.RepositoryName,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (Exception)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    public async Task<IssueRepositoryBindingResult> ChangeRepositoryAsync(
        RepositoryCommandPayload.Change payload, string commandId, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        var pending = await AcquireFenceAsync(
            RepositoryCommandPayloadKinds.Change,
            payload.RepositoryName,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        await CoordinatorProbe.AfterFencePersistedAsync(
            CoordinatorProbeKind.Change, ProjectId, commandId);

        try
        {
            var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(payload.ProjectId, payload.IssueNumber));
            var outcome = await participant.ChangeRepositoryAsync(payload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                Code: MapApplied(outcome),
                RepositoryName: payload.RepositoryName,
                AppliedRevision: pending.CapturedRevision);
        }
        catch (IssueRepositoryLockedException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryLocked,
                payload.RepositoryName,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (IssueRepositoryUnknownException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryUnknown,
                payload.RepositoryName,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (IssueRepositoryStaleRevisionException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryStaleRevision,
                payload.RepositoryName,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (Exception)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    public async Task<IssueRepositoryBindingResult> ReopenAsync(
        RepositoryCommandPayload.Reopen payload, string commandId, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        var pending = await AcquireFenceAsync(
            RepositoryCommandPayloadKinds.Reopen,
            payload.RepositoryName ?? string.Empty,
            commandId,
            expectedRevision,
            payload);

        if (pending.Replay is not null)
            return pending.Replay;

        await CoordinatorProbe.AfterFencePersistedAsync(
            CoordinatorProbeKind.Reopen, ProjectId, commandId);

        try
        {
            var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(payload.ProjectId, payload.IssueNumber));
            var outcome = await participant.ReopenAsync(payload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                Code: MapApplied(outcome),
                RepositoryName: payload.RepositoryName ?? string.Empty,
                AppliedRevision: pending.CapturedRevision);
        }
        catch (IssueRepositoryMissingOnReopenException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryMissingOnReopen,
                payload.RepositoryName ?? string.Empty,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (IssueRepositoryStaleRevisionException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryStaleRevision,
                payload.RepositoryName ?? string.Empty,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (Exception)
        {
            await ClearFenceAsync(commandId);
            throw;
        }
    }

    public async Task<IssueRepositoryBindingResult> RemoveRepositoryAsync(
        RepositoryCommandPayload.Remove payload, string commandId, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        var requestedRepository = payload.RepositoryName ?? string.Empty;

        var project = await _grains.GetGrain<IProjectGrain>(payload.ProjectId).GetAsync();
        var repository = project?.GetRepository(requestedRepository);
        if (repository is null)
        {
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryNotFound,
                requestedRepository,
                expectedRevision ?? 0L,
                $"Repository '{requestedRepository}' not found in project '{payload.ProjectId}'");
        }

        var canonicalRepository = repository.Name;
        var canonicalPayload = new RepositoryCommandPayload.Remove(payload.ProjectId, canonicalRepository);

        if (repository.IsDefault)
        {
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryDefault,
                repository.Name,
                expectedRevision ?? 0L,
                $"Repository '{repository.Name}' is the default. Run 'mo repo set-default <other-name>' first.");
        }

        // Check committed Issue blockers before fencing so a rejection
        // does not leave technical coordinator state behind. Existence and
        // default precedence are established above and revalidated by the
        // Project participant before it mutates Project state.
        if (await _blockerQuery.HasBlockerAsync(payload.ProjectId, canonicalRepository))
        {
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryInUse,
                canonicalRepository,
                expectedRevision ?? 0L);
        }

        var pending = await AcquireFenceAsync(
            RepositoryCommandPayloadKinds.Remove,
            canonicalRepository,
            commandId,
            expectedRevision,
            canonicalPayload);

        if (pending.Replay is not null)
            return pending.Replay;

        await CoordinatorProbe.AfterFencePersistedAsync(
            CoordinatorProbeKind.Remove, ProjectId, commandId);

        try
        {
            var participant = _grains.GetGrain<IProjectBindingParticipant>(payload.ProjectId);
            var outcome = await participant.RemoveRepositoryAsync(canonicalPayload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                Code: MapApplied(outcome),
                RepositoryName: canonicalRepository,
                AppliedRevision: pending.CapturedRevision);
        }
        catch (ProjectRepositoryNotFoundException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryNotFound,
                canonicalRepository,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (ProjectRepositoryStaleRevisionException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryStaleRevision,
                canonicalRepository,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (RepositoryInUseException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryInUse,
                canonicalRepository,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryDefault,
                canonicalRepository,
                pending.CapturedRevision,
                ex.Message);
        }
        catch (ArgumentException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryNotFound,
                canonicalRepository,
                pending.CapturedRevision,
                ex.Message);
        }
    }

    public async Task<IssueRepositoryBindingResult> UpdateRepositoryAsync(
        RepositoryCommandPayload.Update payload, string commandId, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("commandId is required", nameof(commandId));

        var requestedRepository = payload.RepositoryName ?? string.Empty;
        var project = await _grains.GetGrain<IProjectGrain>(payload.ProjectId).GetAsync();
        var repository = project?.GetRepository(requestedRepository);
        if (repository is null)
        {
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryNotFound,
                requestedRepository,
                expectedRevision ?? 0L,
                $"Repository '{requestedRepository}' not found in project '{payload.ProjectId}'");
        }

        var canonicalRepository = repository.Name;
        var canonicalPayload = payload with { RepositoryName = canonicalRepository };
        if (await _blockerQuery.HasBlockerAsync(payload.ProjectId, canonicalRepository))
        {
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryInUse,
                canonicalRepository,
                expectedRevision ?? 0L);
        }

        var pending = await AcquireFenceAsync(
            RepositoryCommandPayloadKinds.Update,
            canonicalRepository,
            commandId,
            expectedRevision,
            canonicalPayload);
        if (pending.Replay is not null)
            return pending.Replay;

        await CoordinatorProbe.AfterFencePersistedAsync(
            CoordinatorProbeKind.Update, ProjectId, commandId);

        try
        {
            var participant = _grains.GetGrain<IProjectBindingParticipant>(payload.ProjectId);
            var outcome = await participant.UpdateRepositoryAsync(canonicalPayload, commandId, pending.CapturedRevision);
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                MapApplied(outcome), canonicalRepository, pending.CapturedRevision);
        }
        catch (ProjectRepositoryNotFoundException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryNotFound, canonicalRepository, pending.CapturedRevision, ex.Message);
        }
        catch (ProjectRepositoryStaleRevisionException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryStaleRevision, canonicalRepository, pending.CapturedRevision, ex.Message);
        }
        catch (RepositoryInUseException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryInUse, canonicalRepository, pending.CapturedRevision, ex.Message);
        }
        catch (ArgumentException ex)
        {
            await ClearFenceAsync(commandId);
            return new IssueRepositoryBindingResult(
                IssueRepositoryBindingResultCode.RepositoryInvalid, canonicalRepository, pending.CapturedRevision, ex.Message);
        }
    }

    private static IssueRepositoryBindingResultCode MapApplied(IssueBindingParticipantOutcome outcome) =>
        outcome == IssueBindingParticipantOutcome.Applied
            ? IssueRepositoryBindingResultCode.Applied
            : IssueRepositoryBindingResultCode.AlreadyApplied;

    private static IssueRepositoryBindingResultCode MapApplied(ProjectBindingParticipantOutcome outcome) =>
        outcome is ProjectBindingParticipantOutcome.Removed or ProjectBindingParticipantOutcome.Updated
            ? IssueRepositoryBindingResultCode.Applied
            : IssueRepositoryBindingResultCode.AlreadyApplied;

    private async Task<FenceDecision> AcquireFenceAsync(
        string kind,
        string repositoryName,
        string commandId,
        long? expectedRevision,
        RepositoryCommandPayload payload)
    {
        var existing = _state.State.Pending;
        if (existing is not null)
        {
            if (string.Equals(existing.CommandId, commandId, StringComparison.Ordinal)
                && existing.Kind == kind
                && string.Equals(existing.RepositoryName, repositoryName, StringComparison.Ordinal))
            {
                // Same command replayed before the participant
                // returned. Proceed with the originally captured
                // revision so the participant sees the same value it
                // saw on the original call.
                return new FenceDecision(existing.ExpectedRevision, null);
            }

            // A different command arrives while the prior one is
            // still in-flight. Replay the survivor to completion
            // (which clears the fence), then proceed with the new
            // command as if it had been the only one.
            await ReplayPendingAsync(existing);
        }

        var capturedRevision = await CaptureRevisionAsync(kind, payload);
        _state.State = _state.State with
        {
            Pending = new PendingRepositoryCommand(
                CommandId: commandId,
                Kind: kind,
                RepositoryName: repositoryName,
                ExpectedRevision: capturedRevision,
                PayloadJson: RepositoryCommandPayloadCodec.Serialize(payload)),
        };
        await _state.WriteStateAsync();
        return new FenceDecision(capturedRevision, null);
    }

    private async Task<long> CaptureRevisionAsync(string kind, RepositoryCommandPayload payload)
    {
        return kind switch
        {
            RepositoryCommandPayloadKinds.Create => await CaptureIssueRevisionAsync(
                ((RepositoryCommandPayload.Create)payload).ProjectId,
                ((RepositoryCommandPayload.Create)payload).IssueNumber),
            RepositoryCommandPayloadKinds.Change => await CaptureIssueRevisionAsync(
                ((RepositoryCommandPayload.Change)payload).ProjectId,
                ((RepositoryCommandPayload.Change)payload).IssueNumber),
            RepositoryCommandPayloadKinds.Reopen => await CaptureIssueRevisionAsync(
                ((RepositoryCommandPayload.Reopen)payload).ProjectId,
                ((RepositoryCommandPayload.Reopen)payload).IssueNumber),
            RepositoryCommandPayloadKinds.Remove => await CaptureProjectRevisionAsync(
                ((RepositoryCommandPayload.Remove)payload).ProjectId),
            RepositoryCommandPayloadKinds.Update => await CaptureProjectRevisionAsync(
                ((RepositoryCommandPayload.Update)payload).ProjectId),
            _ => throw new InvalidOperationException($"Unknown coordinator kind '{kind}'"),
        };
    }

    private Task<long> CaptureIssueRevisionAsync(string projectId, int issueNumber)
    {
        return _grains.GetGrain<IIssueBindingTarget>(IssueGrainKey(projectId, issueNumber)).GetRepositoryBindingRevisionAsync();
    }

    private static string IssueGrainKey(string projectId, int issueNumber) =>
        GrainKey.Issue(new IssueKey(projectId, issueNumber));

    private Task<long> CaptureProjectRevisionAsync(string projectId)
    {
        return _grains.GetGrain<IProjectGrain>(projectId).GetRepositoryBindingRevisionAsync();
    }

    private async Task ReplayPendingAsync(PendingRepositoryCommand pending)
    {
        var payload = RepositoryCommandPayloadCodec.Deserialize(pending.Kind, pending.PayloadJson);
        try
        {
            switch (pending.Kind)
            {
                case RepositoryCommandPayloadKinds.Create:
                {
                    var p = (RepositoryCommandPayload.Create)payload;
                    var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(p.ProjectId, p.IssueNumber));
                    await participant.CreateAsync(p, pending.CommandId, pending.ExpectedRevision);
                    break;
                }
                case RepositoryCommandPayloadKinds.Change:
                {
                    var p = (RepositoryCommandPayload.Change)payload;
                    var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(p.ProjectId, p.IssueNumber));
                    await participant.ChangeRepositoryAsync(p, pending.CommandId, pending.ExpectedRevision);
                    break;
                }
                case RepositoryCommandPayloadKinds.Reopen:
                {
                    var p = (RepositoryCommandPayload.Reopen)payload;
                    var participant = _grains.GetGrain<IIssueBindingParticipant>(IssueGrainKey(p.ProjectId, p.IssueNumber));
                    await participant.ReopenAsync(p, pending.CommandId, pending.ExpectedRevision);
                    break;
                }
                case RepositoryCommandPayloadKinds.Remove:
                {
                    var p = (RepositoryCommandPayload.Remove)payload;
                    var projectId = string.IsNullOrEmpty(p.ProjectId) ? ProjectId : p.ProjectId;
                    var participant = _grains.GetGrain<IProjectBindingParticipant>(projectId);
                    await participant.RemoveRepositoryAsync(
                        new RepositoryCommandPayload.Remove(projectId, pending.RepositoryName),
                        pending.CommandId,
                        pending.ExpectedRevision);
                    break;
                }
                case RepositoryCommandPayloadKinds.Update:
                {
                    var p = (RepositoryCommandPayload.Update)payload;
                    var projectId = string.IsNullOrEmpty(p.ProjectId) ? ProjectId : p.ProjectId;
                    var participant = _grains.GetGrain<IProjectBindingParticipant>(projectId);
                    await participant.UpdateRepositoryAsync(
                        p with { ProjectId = projectId, RepositoryName = pending.RepositoryName },
                        pending.CommandId,
                        pending.ExpectedRevision);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogInformation(
                "IssueRepositoryCoordinator {ProjectId} replay of command {CommandId} ({Kind}) terminated with {Exception}",
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
                "IssueRepositoryCoordinator {ProjectId} fence command mismatch on clear: expected {CommandId} found {Found}",
                ProjectId, commandId, existing.CommandId);
            return;
        }
        _state.State = _state.State with { Pending = null };
        await _state.WriteStateAsync();
    }

    private readonly record struct FenceDecision(
        long CapturedRevision,
        IssueRepositoryBindingResult? Replay);
}

public enum CoordinatorProbeKind
{
    Create = 0,
    Change = 1,
    Reopen = 2,
    Remove = 3,
    Update = 4,
}

/// <summary>
/// issue-417 T-005: test-only static probe that runs synchronously
/// after the coordinator persists its fence but before it invokes the
/// participant. Tests set this hook to await on a TaskCompletionSource
/// so the test can force the "between fence persistence and
/// participant commit" timing without wall-clock waits. Production
/// callers MUST NOT touch this — leaving the probe null is the
/// intended zero-overhead path.
/// </summary>
public static class CoordinatorProbe
{
    private static Func<CoordinatorProbeKind, string, string, Task>? _hook;

    public static IDisposable Install(Func<CoordinatorProbeKind, string, string, Task> hook)
    {
        _hook = hook;
        return new ResetOnDispose();
    }

    public static Task AfterFencePersistedAsync(CoordinatorProbeKind kind, string projectId, string commandId)
    {
        var hook = _hook;
        return hook is null ? Task.CompletedTask : hook(kind, projectId, commandId);
    }

    private sealed class ResetOnDispose : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _hook = null;
        }
    }
}
