using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public interface ISessionTreeMutationFenceGrain : IGrainWithStringKey
{
    Task<SessionTreeMutationFence> GetAsync();
    Task<SessionTreeMutationResult> ReserveAsync(ReserveSessionTreeLinkCommand command);
    Task<SessionTreeMutationResult> BeginFinalizeAsync(string commandId, string edgeId);
    Task<SessionTreeMutationResult> AcknowledgeFinalizeAsync(SessionTreeAttachReceipt receipt);
    Task<SessionTreeMutationResult> CommitFinalizeAsync(string commandId, string edgeId, long revision);
    Task<SessionTreeMutationResult> RejectAsync(string commandId, string edgeId, string reason);
    Task<SessionTreeDetachMutationResult> BeginDetachAsync(BeginSessionTreeDetachCommand command);
    Task<SessionTreeDetachMutationResult> AcknowledgeDetachAsync(SessionTreeDetachReceipt receipt);
    Task<SessionTreeDetachMutationResult> CommitDetachAsync(string commandId, string edgeId, long revision);
    Task<SessionTreeStopSnapshotResult> BeginStopSnapshotAsync(BeginSessionTreeStopSnapshotCommand command);
    Task<SessionTreeStopAdmissionResult> SetStopAdmissionAsync(string operationId, SessionTreeStopAdmissionOutcome outcome);
}

public sealed class SessionTreeMutationFenceGrain(
    [PersistentState("session-tree-mutation-fence")] IPersistentState<SessionTreeMutationFence> state,
    IDbContextFactory<MohistDbContext> dbFactory,
    TimeProvider timeProvider,
    ISessionTreeMutationFenceReadPort snapshotReader)
    : Grain, ISessionTreeMutationFenceGrain
{
    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        var revision = state.State?.GraphRevision ?? 0;
        if (revision > 0)
            await SessionTreeGraphRevisionWatermark.PublishAsync(
                dbFactory, this.GetPrimaryKeyString(), revision, timeProvider.GetUtcNow(), ct);
    }

    public async Task<SessionTreeMutationFence> GetAsync()
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        return state.State ?? new SessionTreeMutationFence(this.GetPrimaryKeyString(), 0);
    }

    public async Task<SessionTreeMutationResult> ReserveAsync(ReserveSessionTreeLinkCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await GetAsync();
        ValidateProject(command.ProjectId);

        var reservations = ReservationsOf(current);
        var existing = reservations.FirstOrDefault(item => item.EdgeId == command.EdgeId);
        if (existing is not null)
        {
            return ReservationMatches(existing, command)
                ? new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, existing.State, existing.RejectionReason)
                : new SessionTreeMutationResult(
                    command.EdgeId,
                    current.GraphRevision,
                    existing.State,
                    "parent_tree_link_command_mismatch",
                    ReconciliationRequired: true);
        }

        if (HasMaterializingSnapshot(current))
            return MutationRejected(command.EdgeId, current.GraphRevision, "stop_snapshot_materializing");
        if (IsParentBlockedByPublishedStop(current, command.ParentSessionId))
            return MutationRejected(command.EdgeId, current.GraphRevision, "parent_tree_stop_in_progress");

        state.State = current with
        {
            Reservation = null,
            PendingMutation = null,
            Reservations = reservations
                .Append(new LinkReservation(
                    command.EdgeId,
                    command.ParentSessionId,
                    command.ChildSessionId,
                    LinkReservationState.Reserved,
                    CommandId: command.CommandId,
                    ChildLaunchJobId: command.ChildLaunchJobId,
                    ExpectedWorkDir: command.ExpectedWorkDir,
                    ExpectedRunnerId: command.ExpectedRunnerId,
                    ExpectedRuntime: command.ExpectedRuntime,
                    ExpectedRuntimeSessionId: command.ExpectedRuntimeSessionId))
                .ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, LinkReservationState.Reserved);
    }

    public async Task<SessionTreeMutationResult> BeginFinalizeAsync(string commandId, string edgeId)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId);
        if (reservation is null)
            return MutationRejected(edgeId, current.GraphRevision, "parent_tree_link_not_reserved");
        if (!string.Equals(reservation.CommandId, commandId, StringComparison.Ordinal))
            return new SessionTreeMutationResult(
                edgeId,
                current.GraphRevision,
                reservation.State,
                "parent_tree_link_command_mismatch",
                ReconciliationRequired: true);
        if (reservation.State == LinkReservationState.Attached)
            return new SessionTreeMutationResult(edgeId, reservation.AttachedRevision ?? current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Rejected)
            return MutationRejected(edgeId, current.GraphRevision, reservation.RejectionReason ?? "parent_tree_link_rejected");
        var pending = PendingOf(current);
        var existing = pending.FirstOrDefault(item => item.EdgeId == edgeId);
        if (existing is not null)
        {
            if (existing.Kind != SessionTreeMutationKind.Attach
                || !string.Equals(existing.CommandId, commandId, StringComparison.Ordinal))
            {
                return new SessionTreeMutationResult(
                    edgeId,
                    current.GraphRevision,
                    reservation.State,
                    "parent_tree_link_command_mismatch",
                    ReconciliationRequired: true);
            }
            return new SessionTreeMutationResult(edgeId, existing.AssignedRevision, reservation.State);
        }

        if (HasMaterializingSnapshot(current)
            || IsParentBlockedByPublishedStop(current, reservation.ParentSessionId))
        {
            return MutationBusy(edgeId, current.GraphRevision, reservation.State);
        }

        if (pending.Any(item => item.AssignedRevision > current.GraphRevision))
            return MutationBusy(edgeId, current.GraphRevision, reservation.State);

        var binding = await snapshotReader.ReadBindingAsync(
            this.GetPrimaryKeyString(),
            reservation.ParentSessionId);
        if (!ParentBindingMatches(this.GetPrimaryKeyString(), reservation, binding))
        {
            const string reason = "parent_binding_changed";
            state.State = current with
            {
                Reservation = null,
                Reservations = reservations
                    .Select(item => item.EdgeId == edgeId
                        ? item with { State = LinkReservationState.Rejected, RejectionReason = reason }
                        : item)
                    .ToArray(),
            };
            await state.WriteStateAsync();
            return MutationRejected(edgeId, current.GraphRevision, reason);
        }

        var mutation = new PendingSessionTreeMutation(
            commandId,
            SessionTreeMutationKind.Attach,
            checked(current.GraphRevision + 1),
            edgeId,
            reservation.ParentSessionId,
            reservation.ChildSessionId,
            reservation.ExpectedWorkDir,
            reservation.ExpectedRunnerId,
            reservation.ExpectedRuntime,
            reservation.ExpectedRuntimeSessionId,
            StopAdmissionActive: false,
            ParticipantAcknowledged: false,
            ChildLaunchJobId: reservation.ChildLaunchJobId);
        state.State = current with
        {
            PendingMutation = null,
            PendingMutations = pending.Append(mutation).ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(edgeId, mutation.AssignedRevision, reservation.State);
    }

    public async Task<SessionTreeMutationResult> AcknowledgeFinalizeAsync(SessionTreeAttachReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var current = await GetAsync();
        var reservation = ReservationsOf(current).FirstOrDefault(item => item.EdgeId == receipt.EdgeId)
            ?? ReservationsOf(current).FirstOrDefault(item => item.CommandId == receipt.CommandId);
        if (reservation is null)
            return MutationRejected(receipt.EdgeId, current.GraphRevision, "parent_tree_link_not_reserved");
        if (reservation.EdgeId != receipt.EdgeId)
            return Reconciliation(receipt.EdgeId, current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Attached)
        {
            return AttachMatches(reservation, receipt)
                ? new SessionTreeMutationResult(receipt.EdgeId, receipt.Revision, LinkReservationState.Attached)
                : Reconciliation(receipt.EdgeId, current.GraphRevision, reservation.State);
        }

        var pending = PendingOf(current);
        var mutation = pending.FirstOrDefault(item => item.EdgeId == receipt.EdgeId);
        if (mutation is null || mutation.Kind != SessionTreeMutationKind.Attach)
            return MutationRejected(receipt.EdgeId, current.GraphRevision, "parent_tree_link_finalize_not_pending");
        if (!AttachMatches(mutation, receipt))
            return Reconciliation(receipt.EdgeId, current.GraphRevision, reservation.State);
        if (mutation.ParticipantAcknowledged)
            return new SessionTreeMutationResult(receipt.EdgeId, receipt.Revision, reservation.State);

        state.State = current with
        {
            PendingMutations = pending
                .Select(item => item.EdgeId == receipt.EdgeId
                    ? item with { ParticipantAcknowledged = true }
                    : item)
                .ToArray(),
            FinalizeReceipts = FinalizeReceiptsOf(current)
                .Append(receipt)
                .DistinctBy(item => item.EdgeId, StringComparer.Ordinal)
                .ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(receipt.EdgeId, receipt.Revision, reservation.State);
    }

    public async Task<SessionTreeMutationResult> CommitFinalizeAsync(
        string commandId,
        string edgeId,
        long revision)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId)
            ?? reservations.FirstOrDefault(item => item.CommandId == commandId);
        if (reservation is null)
            return MutationRejected(edgeId, current.GraphRevision, "parent_tree_link_not_reserved");
        if (reservation.EdgeId != edgeId)
            return Reconciliation(edgeId, current.GraphRevision, reservation.State);
        if (!string.Equals(reservation.CommandId, commandId, StringComparison.Ordinal))
            return Reconciliation(edgeId, current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Attached)
        {
            if (reservation.AttachedRevision != revision)
                return Reconciliation(edgeId, current.GraphRevision, reservation.State);
            await SessionTreeGraphRevisionWatermark.PublishAsync(
                dbFactory, this.GetPrimaryKeyString(), revision, timeProvider.GetUtcNow(), default);
            return new SessionTreeMutationResult(edgeId, revision, LinkReservationState.Attached);
        }
        if (reservation.State == LinkReservationState.Rejected)
            return MutationRejected(edgeId, current.GraphRevision, reservation.RejectionReason ?? "parent_tree_link_rejected");

        var pending = PendingOf(current);
        var mutation = pending.FirstOrDefault(item => item.EdgeId == edgeId);
        var receipt = FinalizeReceiptsOf(current).FirstOrDefault(item => item.EdgeId == edgeId);
        if (mutation is null
            || mutation.Kind != SessionTreeMutationKind.Attach
            || !string.Equals(mutation.CommandId, commandId, StringComparison.Ordinal)
            || mutation.AssignedRevision != revision)
        {
            return Reconciliation(edgeId, current.GraphRevision, reservation.State);
        }
        if (!mutation.ParticipantAcknowledged || receipt is null || !AttachMatches(mutation, receipt))
            return MutationRejected(edgeId, current.GraphRevision, "parent_tree_link_not_acknowledged");

        state.State = current with
        {
            GraphRevision = revision,
            Reservation = null,
            PendingMutation = null,
            Reservations = reservations
                .Select(item => item.EdgeId == edgeId
                    ? item with { State = LinkReservationState.Attached, AttachedRevision = revision }
                    : item)
                .ToArray(),
            PendingMutations = pending.Where(item => item.EdgeId != edgeId).ToArray(),
        };
        await state.WriteStateAsync();
        await SessionTreeGraphRevisionWatermark.PublishAsync(
            dbFactory, this.GetPrimaryKeyString(), revision, timeProvider.GetUtcNow(), default);
        return new SessionTreeMutationResult(edgeId, revision, LinkReservationState.Attached);
    }

    public async Task<SessionTreeMutationResult> RejectAsync(string commandId, string edgeId, string reason)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId);
        if (reservation is null)
            return MutationRejected(edgeId, current.GraphRevision, "parent_tree_link_not_reserved");
        if (!string.Equals(reservation.CommandId, commandId, StringComparison.Ordinal))
            return Reconciliation(edgeId, current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Rejected)
            return MutationRejected(edgeId, current.GraphRevision, reservation.RejectionReason ?? reason);

        state.State = current with
        {
            Reservation = null,
            PendingMutation = null,
            Reservations = reservations
                .Select(item => item.EdgeId == edgeId
                    ? item with { State = LinkReservationState.Rejected, RejectionReason = reason }
                    : item)
                .ToArray(),
            PendingMutations = PendingOf(current).Where(item => item.EdgeId != edgeId).ToArray(),
        };
        await state.WriteStateAsync();
        return MutationRejected(edgeId, current.GraphRevision, reason);
    }

    public async Task<SessionTreeDetachMutationResult> BeginDetachAsync(BeginSessionTreeDetachCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateProject(command.ProjectId);
        if (command.ExpectedAttachedRevision <= 0)
            return RejectedDetach(command.EdgeId, (await GetAsync()).GraphRevision, "parent_tree_link_attached_revision_invalid");

        var current = await GetAsync();
        var receipts = DetachReceiptsOf(current);
        var receipt = receipts.FirstOrDefault(item => item.EdgeId == command.EdgeId);
        if (receipt is not null)
            return DetachReceiptMatches(receipt, command)
                ? new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Detached, command.EdgeId, receipt.Revision)
                : ReconciliationDetach(command.EdgeId, current.GraphRevision);

        var pending = PendingOf(current);
        var existing = pending.FirstOrDefault(item => item.EdgeId == command.EdgeId);
        if (existing is not null)
        {
            return DetachMatches(existing, command)
                ? new SessionTreeDetachMutationResult(
                    existing.ParticipantAcknowledged
                        ? SessionTreeDetachMutationState.Acknowledged
                        : SessionTreeDetachMutationState.Pending,
                    command.EdgeId,
                    existing.AssignedRevision)
                : ReconciliationDetach(command.EdgeId, current.GraphRevision);
        }
        if (pending.Any(item => item.AssignedRevision > current.GraphRevision))
            return RejectedDetach(command.EdgeId, current.GraphRevision, "session_tree_mutation_busy");
        if (HasMaterializingSnapshot(current))
        {
            return RejectedDetach(command.EdgeId, current.GraphRevision, "parent_tree_mutation_busy");
        }

        var reservation = ReservationsOf(current).FirstOrDefault(item => item.EdgeId == command.EdgeId);
        if (reservation is null || reservation.State != LinkReservationState.Attached)
            return RejectedDetach(command.EdgeId, current.GraphRevision, "parent_tree_link_not_attached");
        if (reservation.ParentSessionId != command.ParentSessionId
            || reservation.ChildSessionId != command.ChildSessionId
            || reservation.ChildLaunchJobId != command.ChildLaunchJobId
            || reservation.AttachedRevision != command.ExpectedAttachedRevision)
        {
            return ReconciliationDetach(command.EdgeId, current.GraphRevision);
        }

        var mutation = new PendingSessionTreeMutation(
            command.CommandId,
            SessionTreeMutationKind.Detach,
            checked(current.GraphRevision + 1),
            command.EdgeId,
            command.ParentSessionId,
            command.ChildSessionId,
            null,
            null,
            null,
            null,
            StopAdmissionActive: false,
            ParticipantAcknowledged: false,
            ChildLaunchJobId: command.ChildLaunchJobId,
            ExpectedAttachedRevision: command.ExpectedAttachedRevision);
        state.State = current with
        {
            PendingMutation = null,
            PendingMutations = pending.Append(mutation).ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeDetachMutationResult(
            SessionTreeDetachMutationState.Pending,
            command.EdgeId,
            mutation.AssignedRevision);
    }

    public async Task<SessionTreeDetachMutationResult> AcknowledgeDetachAsync(SessionTreeDetachReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var current = await GetAsync();
        var committed = DetachReceiptsOf(current).FirstOrDefault(item => item.EdgeId == receipt.EdgeId);
        if (committed is not null)
            return DetachReceiptMatches(committed, receipt)
                ? new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Detached, receipt.EdgeId, receipt.Revision)
                : ReconciliationDetach(receipt.EdgeId, current.GraphRevision);

        var mutation = PendingOf(current).FirstOrDefault(item => item.EdgeId == receipt.EdgeId)
            ?? PendingOf(current).FirstOrDefault(item => item.CommandId == receipt.CommandId);
        if (mutation is null || mutation.Kind != SessionTreeMutationKind.Detach)
            return RejectedDetach(receipt.EdgeId, current.GraphRevision, "session_tree_detach_not_pending");
        if (mutation.EdgeId != receipt.EdgeId)
            return ReconciliationDetach(receipt.EdgeId, current.GraphRevision);
        if (!DetachMatches(mutation, receipt))
            return ReconciliationDetach(receipt.EdgeId, current.GraphRevision);
        if (mutation.ParticipantAcknowledged)
            return new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Acknowledged, receipt.EdgeId, receipt.Revision);

        state.State = current with
        {
            PendingMutations = PendingOf(current)
                .Select(item => item.EdgeId == receipt.EdgeId
                    ? item with { ParticipantAcknowledged = true }
                    : item)
                .ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Acknowledged, receipt.EdgeId, receipt.Revision);
    }

    public async Task<SessionTreeDetachMutationResult> CommitDetachAsync(
        string commandId,
        string edgeId,
        long revision)
    {
        var current = await GetAsync();
        var committed = DetachReceiptsOf(current).FirstOrDefault(item => item.EdgeId == edgeId);
        if (committed is not null)
            return committed.CommandId == commandId && committed.Revision == revision
                ? new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Detached, edgeId, revision)
                : ReconciliationDetach(edgeId, current.GraphRevision);

        var mutation = PendingOf(current).FirstOrDefault(item => item.EdgeId == edgeId)
            ?? PendingOf(current).FirstOrDefault(item => item.CommandId == commandId);
        if (mutation is null || mutation.Kind != SessionTreeMutationKind.Detach)
            return RejectedDetach(edgeId, current.GraphRevision, "session_tree_detach_not_pending");
        if (mutation.EdgeId != edgeId)
            return ReconciliationDetach(edgeId, current.GraphRevision);
        if (!string.Equals(mutation.CommandId, commandId, StringComparison.Ordinal)
            || mutation.AssignedRevision != revision)
            return ReconciliationDetach(edgeId, current.GraphRevision);
        if (!mutation.ParticipantAcknowledged)
            return RejectedDetach(edgeId, current.GraphRevision, "session_tree_detach_not_acknowledged");

        var receipt = new SessionTreeDetachReceipt(
            commandId,
            mutation.EdgeId,
            mutation.ParentSessionId,
            mutation.ChildSessionId,
            mutation.AssignedRevision,
            mutation.ChildLaunchJobId ?? string.Empty,
            mutation.ExpectedAttachedRevision ?? 0);
        state.State = current with
        {
            GraphRevision = revision,
            PendingMutation = null,
            PendingMutations = PendingOf(current).Where(item => item.EdgeId != edgeId).ToArray(),
            DetachReceipts = DetachReceiptsOf(current).Append(receipt).ToArray(),
        };
        await state.WriteStateAsync();
        await SessionTreeGraphRevisionWatermark.PublishAsync(
            dbFactory, this.GetPrimaryKeyString(), revision, timeProvider.GetUtcNow(), default);
        return new SessionTreeDetachMutationResult(SessionTreeDetachMutationState.Detached, edgeId, revision);
    }

    public async Task<SessionTreeStopSnapshotResult> BeginStopSnapshotAsync(BeginSessionTreeStopSnapshotCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateProject(command.ProjectId);
        ValidateStopIdentity(command);

        var current = await GetAsync();
        var snapshots = StopSnapshotsOf(current);
        var prior = snapshots.FirstOrDefault(item =>
            item.OperationId == command.OperationId || item.IdempotencyKey == command.IdempotencyKey);
        if (prior is not null)
        {
            if (!SameStopIdentity(prior, command))
                return new SessionTreeStopSnapshotResult(
                    SessionTreeStopSnapshotDisposition.Rejected,
                    RejectionReason: "stop_operation_conflict");
            if (prior.Phase == SessionTreeStopSnapshotPhase.Frozen)
                return new SessionTreeStopSnapshotResult(SessionTreeStopSnapshotDisposition.Replayed, prior);
            return await MaterializeStopSnapshotAsync(current, snapshots, prior, command);
        }

        if (HasMaterializingSnapshot(current))
            return new SessionTreeStopSnapshotResult(
                SessionTreeStopSnapshotDisposition.Blocked,
                RejectionReason: "stop_snapshot_materializing");
        if (PendingOf(current).Any(item => item.AssignedRevision > current.GraphRevision))
            return new SessionTreeStopSnapshotResult(
                SessionTreeStopSnapshotDisposition.Blocked,
                RejectionReason: "session_tree_mutation_pending");

        var materializing = new SessionTreeStopSnapshot(
            command.ProjectId,
            command.RootSessionId,
            command.OperationId,
            command.IdempotencyKey,
            command.RequestFingerprint,
            current.GraphRevision,
            [],
            [],
            SessionTreeStopAdmissionOutcome.Running,
            SessionTreeStopSnapshotPhase.Materializing);
        state.State = current with { StopSnapshots = snapshots.Append(materializing).ToArray() };
        await state.WriteStateAsync();
        return await MaterializeStopSnapshotAsync(state.State, snapshots.Append(materializing).ToArray(), materializing, command);
    }

    public async Task<SessionTreeStopAdmissionResult> SetStopAdmissionAsync(
        string operationId,
        SessionTreeStopAdmissionOutcome outcome)
    {
        var current = await GetAsync();
        var snapshots = StopSnapshotsOf(current);
        var index = snapshots.FindIndex(item => item.OperationId == operationId);
        if (index < 0)
            return new SessionTreeStopAdmissionResult(false, RejectionReason: "stop_operation_not_found");
        var snapshot = snapshots[index];
        if (snapshot.Phase != SessionTreeStopSnapshotPhase.Frozen)
            return new SessionTreeStopAdmissionResult(true, operationId, "stop_snapshot_materializing");
        if (snapshot.AdmissionOutcome is SessionTreeStopAdmissionOutcome.Completed or SessionTreeStopAdmissionOutcome.Partial)
        {
            return new SessionTreeStopAdmissionResult(
                false,
                operationId,
                outcome is SessionTreeStopAdmissionOutcome.Completed or SessionTreeStopAdmissionOutcome.Partial
                    ? null
                    : "stop_operation_terminal");
        }

        snapshots[index] = snapshot with { AdmissionOutcome = outcome };
        var active = snapshots.Any(item =>
            item.Phase == SessionTreeStopSnapshotPhase.Frozen
            && item.AdmissionOutcome is SessionTreeStopAdmissionOutcome.Running or SessionTreeStopAdmissionOutcome.Unknown);
        state.State = current with { ActiveTreeStop = active, StopSnapshots = snapshots.ToArray() };
        await state.WriteStateAsync();
        return new SessionTreeStopAdmissionResult(active, operationId);
    }

    private async Task<SessionTreeStopSnapshotResult> MaterializeStopSnapshotAsync(
        SessionTreeMutationFence current,
        IReadOnlyList<SessionTreeStopSnapshot> snapshots,
        SessionTreeStopSnapshot materializing,
        BeginSessionTreeStopSnapshotCommand command)
    {
        var facts = await snapshotReader.ReadAtAsync(
            command.ProjectId,
            command.RootSessionId,
            materializing.GraphRevision);
        ValidateFacts(facts, materializing);
        var targets = facts.Targets
            .Select(item => new SessionTreeStopTargetSnapshot(
                item.SessionId,
                item.TurnId,
                item.JobId,
                item.TurnStatus,
                item.RunnerId,
                item.Runtime,
                item.RuntimeSessionId,
                item.WorkDir,
                SessionTreeStopOperationIds.ForTarget(command.OperationId, item.SessionId)))
            .ToArray();
        var frozen = materializing with
        {
            Membership = facts.Membership.ToArray(),
            Targets = targets,
            Phase = SessionTreeStopSnapshotPhase.Frozen,
        };
        var nextSnapshots = snapshots
            .Select(item => item.OperationId == materializing.OperationId ? frozen : item)
            .ToArray();
        var membershipIds = facts.Membership
            .Select(item => item.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var nextReservations = ReservationsOf(current)
            .Select(item => item.State == LinkReservationState.Reserved
                && membershipIds.Contains(item.ParentSessionId)
                ? item with
                {
                    State = LinkReservationState.Rejected,
                    RejectionReason = "parent_tree_stop_in_progress",
                }
                : item)
            .ToArray();
        state.State = current with
        {
            ActiveTreeStop = frozen.AdmissionOutcome is SessionTreeStopAdmissionOutcome.Running
                or SessionTreeStopAdmissionOutcome.Unknown,
            StopSnapshots = nextSnapshots,
            Reservations = nextReservations,
            Reservation = null,
        };
        await state.WriteStateAsync();
        return new SessionTreeStopSnapshotResult(SessionTreeStopSnapshotDisposition.Started, frozen);
    }

    private static void ValidateFacts(SessionTreeStopSnapshotFacts facts, SessionTreeStopSnapshot materializing)
    {
        if (facts.ProjectId != materializing.ProjectId
            || facts.RootSessionId != materializing.RootSessionId
            || facts.GraphRevision != materializing.GraphRevision)
        {
            throw new InvalidOperationException("Session tree snapshot facts do not match the materializing fence command.");
        }
        if (facts.Membership.Count == 0
            || facts.Membership.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != facts.Membership.Count
            || facts.Targets.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != facts.Targets.Count)
        {
            throw new InvalidOperationException("Session tree snapshot facts contain duplicate or empty identities.");
        }
        if (!facts.Membership.Any(item => item.SessionId == materializing.RootSessionId && item.ParentSessionId is null))
            throw new InvalidOperationException("Session tree snapshot facts must include the root.");
        var membership = facts.Membership.Select(item => item.SessionId).ToHashSet(StringComparer.Ordinal);
        if (facts.Targets.Any(item => !membership.Contains(item.SessionId)))
            throw new InvalidOperationException("Session tree snapshot target facts must belong to the membership.");
    }

    private static bool ReservationMatches(LinkReservation item, ReserveSessionTreeLinkCommand command) =>
        item.CommandId == command.CommandId
        && item.ParentSessionId == command.ParentSessionId
        && item.ChildSessionId == command.ChildSessionId
        && item.ChildLaunchJobId == command.ChildLaunchJobId
        && item.ExpectedWorkDir == command.ExpectedWorkDir
        && item.ExpectedRunnerId == command.ExpectedRunnerId
        && item.ExpectedRuntime == command.ExpectedRuntime
        && item.ExpectedRuntimeSessionId == command.ExpectedRuntimeSessionId;

    private static bool AttachMatches(PendingSessionTreeMutation mutation, SessionTreeAttachReceipt receipt) =>
        mutation.CommandId == receipt.CommandId
        && mutation.EdgeId == receipt.EdgeId
        && mutation.ParentSessionId == receipt.ParentSessionId
        && mutation.ChildSessionId == receipt.ChildSessionId
        && mutation.ChildLaunchJobId == receipt.ChildLaunchJobId
        && mutation.AssignedRevision == receipt.Revision;

    private static bool AttachMatches(LinkReservation reservation, SessionTreeAttachReceipt receipt) =>
        reservation.CommandId == receipt.CommandId
        && reservation.EdgeId == receipt.EdgeId
        && reservation.ParentSessionId == receipt.ParentSessionId
        && reservation.ChildSessionId == receipt.ChildSessionId
        && reservation.ChildLaunchJobId == receipt.ChildLaunchJobId
        && reservation.AttachedRevision == receipt.Revision;

    private static bool ParentBindingMatches(
        string projectId,
        LinkReservation reservation,
        SessionTreeSessionBindingFact? binding) =>
        binding is not null
        && binding.ProjectId == projectId
        && binding.SessionId == reservation.ParentSessionId
        && binding.WorkDir == reservation.ExpectedWorkDir
        && binding.RunnerId == reservation.ExpectedRunnerId
        && binding.Runtime == reservation.ExpectedRuntime
        && binding.RuntimeSessionId == reservation.ExpectedRuntimeSessionId;

    private static bool DetachMatches(PendingSessionTreeMutation mutation, BeginSessionTreeDetachCommand command) =>
        mutation.CommandId == command.CommandId
        && mutation.EdgeId == command.EdgeId
        && mutation.ParentSessionId == command.ParentSessionId
        && mutation.ChildSessionId == command.ChildSessionId
        && mutation.ChildLaunchJobId == command.ChildLaunchJobId
        && mutation.ExpectedAttachedRevision == command.ExpectedAttachedRevision;

    private static bool DetachMatches(PendingSessionTreeMutation mutation, SessionTreeDetachReceipt receipt) =>
        mutation.CommandId == receipt.CommandId
        && mutation.EdgeId == receipt.EdgeId
        && mutation.ParentSessionId == receipt.ParentSessionId
        && mutation.ChildSessionId == receipt.ChildSessionId
        && mutation.ChildLaunchJobId == receipt.ChildLaunchJobId
        && mutation.AssignedRevision == receipt.Revision
        && mutation.ExpectedAttachedRevision == receipt.ExpectedAttachedRevision;

    private static bool DetachReceiptMatches(SessionTreeDetachReceipt left, BeginSessionTreeDetachCommand right) =>
        left.CommandId == right.CommandId
        && left.EdgeId == right.EdgeId
        && left.ParentSessionId == right.ParentSessionId
        && left.ChildSessionId == right.ChildSessionId
        && left.ChildLaunchJobId == right.ChildLaunchJobId
        && left.ExpectedAttachedRevision == right.ExpectedAttachedRevision;

    private static bool DetachReceiptMatches(SessionTreeDetachReceipt left, SessionTreeDetachReceipt right) =>
        left == right;

    private static bool SameStopIdentity(SessionTreeStopSnapshot prior, BeginSessionTreeStopSnapshotCommand command) =>
        prior.ProjectId == command.ProjectId
        && prior.RootSessionId == command.RootSessionId
        && prior.OperationId == command.OperationId
        && prior.IdempotencyKey == command.IdempotencyKey
        && prior.RequestFingerprint == command.RequestFingerprint;

    private bool IsParentBlockedByPublishedStop(SessionTreeMutationFence current, string parentSessionId) =>
        StopSnapshotsOf(current).Any(snapshot =>
            snapshot.Phase == SessionTreeStopSnapshotPhase.Frozen
            && snapshot.AdmissionOutcome is SessionTreeStopAdmissionOutcome.Running or SessionTreeStopAdmissionOutcome.Unknown
            && snapshot.Membership.Any(member => member.SessionId == parentSessionId));

    private static bool HasMaterializingSnapshot(SessionTreeMutationFence current) =>
        StopSnapshotsOf(current).Any(item => item.Phase == SessionTreeStopSnapshotPhase.Materializing);

    private static IReadOnlyList<LinkReservation> ReservationsOf(SessionTreeMutationFence current) =>
        current.Reservations is { Count: > 0 }
            ? current.Reservations
            : current.Reservation is { } legacy ? [legacy] : [];

    private static IReadOnlyList<PendingSessionTreeMutation> PendingOf(SessionTreeMutationFence current) =>
        current.PendingMutations is { Count: > 0 }
            ? current.PendingMutations
            : current.PendingMutation is { } legacy ? [legacy] : [];

    private static IReadOnlyList<SessionTreeDetachReceipt> DetachReceiptsOf(SessionTreeMutationFence current) =>
        current.DetachReceipts ?? [];

    private static IReadOnlyList<SessionTreeAttachReceipt> FinalizeReceiptsOf(SessionTreeMutationFence current) =>
        current.FinalizeReceipts ?? [];

    private static List<SessionTreeStopSnapshot> StopSnapshotsOf(SessionTreeMutationFence current) =>
        current.StopSnapshots?.ToList() ?? [];

    private void ValidateProject(string projectId)
    {
        if (!string.Equals(projectId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Session tree mutation project does not match the fence key.");
    }

    private static void ValidateStopIdentity(BeginSessionTreeStopSnapshotCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.RootSessionId)
            || string.IsNullOrWhiteSpace(command.OperationId)
            || string.IsNullOrWhiteSpace(command.IdempotencyKey)
            || string.IsNullOrWhiteSpace(command.RequestFingerprint))
        {
            throw new InvalidOperationException("A stop snapshot requires a complete identity.");
        }
    }

    private static SessionTreeMutationResult MutationRejected(string edgeId, long revision, string reason) =>
        new(edgeId, revision, LinkReservationState.Rejected, reason);

    private static SessionTreeMutationResult MutationBusy(
        string edgeId,
        long revision,
        LinkReservationState state) =>
        new(edgeId, revision, state, "session_tree_mutation_busy");

    private static SessionTreeMutationResult Reconciliation(string edgeId, long revision, LinkReservationState state) =>
        new(edgeId, revision, state, "reconciliation_required", ReconciliationRequired: true);

    private static SessionTreeDetachMutationResult RejectedDetach(string edgeId, long revision, string reason) =>
        new(SessionTreeDetachMutationState.Rejected, edgeId, revision, reason);

    private static SessionTreeDetachMutationResult ReconciliationDetach(string edgeId, long revision) =>
        new(SessionTreeDetachMutationState.ReconciliationRequired, edgeId, revision, "reconciliation_required");
}
