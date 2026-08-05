using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public interface ISessionTreeMutationFenceGrain : IGrainWithStringKey
{
    Task<SessionTreeMutationFence> GetAsync();
    Task<SessionTreeMutationResult> ReserveAsync(ReserveSessionTreeLinkCommand command);
    Task<SessionTreeMutationResult> BeginFinalizeAsync(string commandId, string edgeId);
    Task<SessionTreeMutationResult> CommitFinalizeAsync(string commandId, string edgeId);
    Task<SessionTreeMutationResult> FinalizeAsync(string commandId, string edgeId);
    Task<SessionTreeMutationResult> RejectAsync(string commandId, string edgeId, string reason);
}

public sealed class SessionTreeMutationFenceGrain(
    [PersistentState("session-tree-mutation-fence")] IPersistentState<SessionTreeMutationFence> state)
    : Grain, ISessionTreeMutationFenceGrain
{
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
        if (!string.Equals(command.ProjectId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
            throw new InvalidOperationException("Session tree mutation project does not match the fence key.");

        var reservations = ReservationsOf(current);
        var pending = PendingOf(current);
        var existing = reservations.FirstOrDefault(item => item.EdgeId == command.EdgeId);
        if (existing is not null)
        {
            var existingPending = pending.FirstOrDefault(item => item.EdgeId == command.EdgeId);
            if (existingPending is not null
                && string.Equals(existingPending.CommandId, command.CommandId, StringComparison.Ordinal))
            {
                return new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, existing.State, existing.RejectionReason);
            }
            return new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, existing.State, existing.RejectionReason);
        }

        if (current.ActiveTreeStop)
            return new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, LinkReservationState.Rejected, "parent_tree_stop_in_progress");

        var nextReservations = reservations
            .Append(new LinkReservation(
                command.EdgeId,
                command.ParentSessionId,
                command.ChildSessionId,
                LinkReservationState.Reserved))
            .ToArray();
        var nextPending = pending
            .Append(new PendingSessionTreeMutation(
                command.CommandId,
                SessionTreeMutationKind.Attach,
                0,
                command.EdgeId,
                command.ParentSessionId,
                command.ChildSessionId,
                command.ExpectedWorkDir,
                command.ExpectedRunnerId,
                command.ExpectedRuntime,
                command.ExpectedRuntimeSessionId,
                current.ActiveTreeStop))
            .ToArray();
        state.State = current with
        {
            Reservation = null,
            PendingMutation = null,
            Reservations = nextReservations,
            PendingMutations = nextPending,
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(command.EdgeId, current.GraphRevision, LinkReservationState.Reserved);
    }

    public async Task<SessionTreeMutationResult> BeginFinalizeAsync(string commandId, string edgeId)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var pending = PendingOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId);
        if (reservation is null)
            throw new InvalidOperationException("The requested session tree mutation is not pending.");
        if (reservation.State == LinkReservationState.Attached)
            return new SessionTreeMutationResult(edgeId, reservation.AttachedRevision ?? current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Rejected)
            return new SessionTreeMutationResult(edgeId, current.GraphRevision, reservation.State, reservation.RejectionReason);

        var mutation = pending.FirstOrDefault(item => item.EdgeId == edgeId);
        if (mutation is null || !string.Equals(mutation.CommandId, commandId, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested session tree mutation is not pending.");

        if (mutation.AssignedRevision > current.GraphRevision)
            return new SessionTreeMutationResult(edgeId, mutation.AssignedRevision, reservation.State);

        if (pending.Any(item => item.EdgeId != edgeId && item.AssignedRevision > current.GraphRevision))
            return new SessionTreeMutationResult(edgeId, current.GraphRevision, reservation.State, "finalize_busy");

        var assignedRevision = checked(current.GraphRevision + 1);
        state.State = current with
        {
            PendingMutations = pending
                .Select(item => item.EdgeId == edgeId
                    ? item with { AssignedRevision = assignedRevision }
                    : item)
                .ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(edgeId, assignedRevision, reservation.State);
    }

    public async Task<SessionTreeMutationResult> CommitFinalizeAsync(string commandId, string edgeId)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var pending = PendingOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId);
        if (reservation is null)
            throw new InvalidOperationException("The requested session tree mutation is not pending.");
        if (reservation.State == LinkReservationState.Attached)
            return new SessionTreeMutationResult(edgeId, reservation.AttachedRevision ?? current.GraphRevision, reservation.State);
        if (reservation.State == LinkReservationState.Rejected)
            return new SessionTreeMutationResult(edgeId, current.GraphRevision, reservation.State, reservation.RejectionReason);
        var mutation = pending.FirstOrDefault(item => item.EdgeId == edgeId);
        if (mutation is null
            || !string.Equals(mutation.CommandId, commandId, StringComparison.Ordinal)
            || mutation.AssignedRevision <= current.GraphRevision)
            throw new InvalidOperationException("The requested session tree mutation has not begun finalization.");

        var attachedRevision = mutation.AssignedRevision;
        state.State = current with
        {
            GraphRevision = attachedRevision,
            Reservation = null,
            PendingMutation = null,
            Reservations = reservations
                .Select(item => item.EdgeId == edgeId
                    ? item with { State = LinkReservationState.Attached, AttachedRevision = attachedRevision }
                    : item)
                .ToArray(),
            PendingMutations = pending.Where(item => item.EdgeId != edgeId).ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(edgeId, attachedRevision, LinkReservationState.Attached);
    }

    public async Task<SessionTreeMutationResult> FinalizeAsync(string commandId, string edgeId)
    {
        await BeginFinalizeAsync(commandId, edgeId);
        return await CommitFinalizeAsync(commandId, edgeId);
    }

    public async Task<SessionTreeMutationResult> RejectAsync(string commandId, string edgeId, string reason)
    {
        var current = await GetAsync();
        var reservations = ReservationsOf(current);
        var pending = PendingOf(current);
        var reservation = reservations.FirstOrDefault(item => item.EdgeId == edgeId);
        if (reservation is null)
            throw new InvalidOperationException("The requested session tree mutation is not pending.");
        if (reservation.State == LinkReservationState.Rejected)
            return new SessionTreeMutationResult(edgeId, current.GraphRevision, reservation.State, reservation.RejectionReason);
        var mutation = pending.FirstOrDefault(item => item.EdgeId == edgeId);
        if (mutation is null || !string.Equals(mutation.CommandId, commandId, StringComparison.Ordinal))
            throw new InvalidOperationException("The requested session tree mutation is not pending.");

        state.State = current with
        {
            Reservation = null,
            PendingMutation = null,
            Reservations = reservations
                .Select(item => item.EdgeId == edgeId
                    ? item with { State = LinkReservationState.Rejected, RejectionReason = reason }
                    : item)
                .ToArray(),
            PendingMutations = pending.Where(item => item.EdgeId != edgeId).ToArray(),
        };
        await state.WriteStateAsync();
        return new SessionTreeMutationResult(edgeId, current.GraphRevision, LinkReservationState.Rejected, reason);
    }

    private static IReadOnlyList<LinkReservation> ReservationsOf(SessionTreeMutationFence current) =>
        current.Reservations is { Count: > 0 }
            ? current.Reservations
            : current.Reservation is { } legacy ? [legacy] : [];

    private static IReadOnlyList<PendingSessionTreeMutation> PendingOf(SessionTreeMutationFence current) =>
        current.PendingMutations is { Count: > 0 }
            ? current.PendingMutations
            : current.PendingMutation is { } legacy ? [legacy] : [];
}
