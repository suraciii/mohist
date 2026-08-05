using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public interface ISessionTreeStopTargetAdapter
{
    Task<SessionTreeStopTargetResult> StopAsync(
        string projectId,
        SessionTreeStopTargetSnapshot target,
        CancellationToken cancellationToken = default);
}

public interface ISessionTreeStopOperationGrain : IGrainWithStringKey
{
    Task<SessionTreeStopOperation> StartAsync(SessionTreeStopRequest request);
    Task<SessionTreeStopOperation> GetAsync();
}

public sealed class SessionTreeStopOperationGrain(
    [PersistentState("session-tree-stop-operation")] IPersistentState<SessionTreeStopOperation> state,
    ISessionTreeStopTargetAdapter targetAdapter)
    : Grain, ISessionTreeStopOperationGrain
{
    public async Task<SessionTreeStopOperation> GetAsync()
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        if (!state.RecordExists
            || state.State is null
            || string.IsNullOrWhiteSpace(state.State.OperationId))
            throw new InvalidOperationException("The stop operation has not been started.");
        return state.State;
    }

    public async Task<SessionTreeStopOperation> StartAsync(SessionTreeStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProjectId)
            || string.IsNullOrWhiteSpace(request.RootSessionId)
            || string.IsNullOrWhiteSpace(request.OperationId)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || string.IsNullOrWhiteSpace(request.RequestFingerprint))
        {
            throw new ArgumentException("A stop request requires a complete public identity.", nameof(request));
        }
        if (!string.Equals(request.OperationId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
            throw new InvalidOperationException("The stop operation key does not match its identity.");

        var operation = await LoadOrCreateAsync(request);
        var fence = GrainFactory.GetGrain<ISessionTreeMutationFenceGrain>(request.ProjectId);
        var snapshotResult = await fence.BeginStopSnapshotAsync(new BeginSessionTreeStopSnapshotCommand(
            request.ProjectId,
            request.RootSessionId,
            request.OperationId,
            request.IdempotencyKey,
            request.RequestFingerprint));
        if (snapshotResult.Snapshot is null)
            return operation;

        operation = operation.Snapshot is null
            ? operation.Publish(snapshotResult.Snapshot)
            : operation.Replay(snapshotResult.Snapshot);
        await SaveAsync(operation);

        if (operation.Status is SessionTreeStopOperationStatus.Completed
            or SessionTreeStopOperationStatus.Partial)
        {
            await fence.SetStopAdmissionAsync(
                operation.OperationId,
                operation.Status == SessionTreeStopOperationStatus.Partial
                    ? SessionTreeStopAdmissionOutcome.Partial
                    : SessionTreeStopAdmissionOutcome.Completed);
            return operation;
        }

        var snapshot = operation.Snapshot
            ?? throw new InvalidOperationException("A non-terminal stop operation requires a frozen snapshot.");
        foreach (var target in snapshot.Targets.OrderBy(item => item.SessionId, StringComparer.Ordinal))
        {
            var existing = operation.TargetResults?.FirstOrDefault(item => item.SessionId == target.SessionId);
            if (existing is not null && existing.Outcome is not (
                    SessionTreeStopTargetOutcome.Unknown
                    or SessionTreeStopTargetOutcome.Pending
                    or SessionTreeStopTargetOutcome.StopRequested))
            {
                continue;
            }

            var result = await targetAdapter.StopAsync(request.ProjectId, target);
            operation = operation.RecordTarget(result);
            await SaveAsync(operation);
            if (result.Outcome == SessionTreeStopTargetOutcome.Unknown)
                break;
        }

        await fence.SetStopAdmissionAsync(
            operation.OperationId,
            operation.Status switch
            {
                SessionTreeStopOperationStatus.Unknown => SessionTreeStopAdmissionOutcome.Unknown,
                SessionTreeStopOperationStatus.Partial => SessionTreeStopAdmissionOutcome.Partial,
                SessionTreeStopOperationStatus.Completed => SessionTreeStopAdmissionOutcome.Completed,
                _ => SessionTreeStopAdmissionOutcome.Running,
            });
        return operation;
    }

    private async Task<SessionTreeStopOperation> LoadOrCreateAsync(SessionTreeStopRequest request)
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        if (!state.RecordExists
            || state.State is null
            || string.IsNullOrWhiteSpace(state.State.OperationId))
        {
            var created = SessionTreeStopOperation.Create(request);
            await SaveAsync(created);
            return created;
        }

        var current = state.State;
        if (current.ProjectId != request.ProjectId
            || current.RootSessionId != request.RootSessionId
            || current.OperationId != request.OperationId
            || current.IdempotencyKey != request.IdempotencyKey
            || current.RequestFingerprint != request.RequestFingerprint)
        {
            throw new SessionTreeStopOperationConflictException(request.OperationId);
        }
        return current;
    }

    private async Task SaveAsync(SessionTreeStopOperation operation)
    {
        state.State = operation;
        await state.WriteStateAsync();
    }
}
