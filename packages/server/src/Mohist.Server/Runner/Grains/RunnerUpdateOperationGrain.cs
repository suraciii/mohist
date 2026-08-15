using Orleans;

namespace Mohist.Server.Runner.Grains;

public interface IRunnerUpdateOperationGrain : IGrainWithStringKey
{
    Task<RunnerUpdateOperation?> GetPendingAsync();
    Task<RunnerUpdateOperation?> GetAsync(string operationId);
    Task<RunnerUpdateOperation> StartOrGetAsync(RunnerUpdateOperation candidate);
    Task<RunnerUpdateOperation> MarkWorkAsync(
        string operationId,
        string ownerKind,
        string ownerId,
        string workId,
        string? taskRunId,
        RunnerUpdateWorkStatus status);
}

/// <summary>
/// Durable update fence for one Runner identity. The grain key is the Runner
/// identity, while the operation id remains stable across retries and is the
/// identity carried to owner-domain events.
/// </summary>
[GenerateSerializer]
public sealed record RunnerUpdateOperation(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string RunnerId,
    [property: Id(2)] DateTimeOffset CreatedAt,
    [property: Id(3)] IReadOnlyList<RunnerUpdateWork> AffectedWorks,
    [property: Id(4)] RunnerUpdateOperationStatus Status = RunnerUpdateOperationStatus.Pending);

[GenerateSerializer]
public sealed record RunnerUpdateWork(
    [property: Id(0)] string OwnerKind,
    [property: Id(1)] string OwnerId,
    [property: Id(2)] string WorkId,
    [property: Id(3)] string? TaskRunId,
    [property: Id(4)] string WorkType,
    [property: Id(5)] RunnerUpdateWorkStatus Status = RunnerUpdateWorkStatus.Pending)
{
    public string Key => string.Join('\u001f', OwnerKind, OwnerId, WorkId, TaskRunId ?? string.Empty);
}

public enum RunnerUpdateOperationStatus
{
    Pending,
    Settled,
}

public enum RunnerUpdateWorkStatus
{
    Pending,
    Marked,
    AlreadyEnded,
    Settled,
}

[GenerateSerializer]
public sealed class RunnerUpdateOperationState
{
    [Id(0)] public List<RunnerUpdateOperation> Operations { get; set; } = [];
}

public sealed class RunnerUpdateOperationGrain(
    [PersistentState("runner-update-operation")] IPersistentState<RunnerUpdateOperationState> state)
    : Grain, IRunnerUpdateOperationGrain
{
    public async Task<RunnerUpdateOperation?> GetPendingAsync()
    {
        await LoadAsync();
        return state.State.Operations.LastOrDefault(operation =>
            operation.Status == RunnerUpdateOperationStatus.Pending);
    }

    public async Task<RunnerUpdateOperation?> GetAsync(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return null;

        await LoadAsync();
        return state.State.Operations.LastOrDefault(operation =>
            string.Equals(operation.OperationId, operationId, StringComparison.Ordinal));
    }

    public async Task<RunnerUpdateOperation> StartOrGetAsync(RunnerUpdateOperation candidate)
    {
        ValidateCandidate(candidate);
        await LoadAsync();

        var existing = state.State.Operations.LastOrDefault(operation =>
            operation.Status == RunnerUpdateOperationStatus.Pending);
        if (existing is not null)
            return existing;

        var duplicate = state.State.Operations.LastOrDefault(operation =>
            string.Equals(operation.OperationId, candidate.OperationId, StringComparison.Ordinal));
        if (duplicate is not null)
            return duplicate;

        var normalized = candidate with
        {
            AffectedWorks = candidate.AffectedWorks
                .Where(IsValidWork)
                .GroupBy(work => work.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray(),
        };
        state.State.Operations.Add(normalized);
        await state.WriteStateAsync();
        return normalized;
    }

    public async Task<RunnerUpdateOperation> MarkWorkAsync(
        string operationId,
        string ownerKind,
        string ownerId,
        string workId,
        string? taskRunId,
        RunnerUpdateWorkStatus status)
    {
        if (status == RunnerUpdateWorkStatus.Pending)
            throw new ArgumentException("A marking call must record a completed owner outcome.", nameof(status));

        await LoadAsync();
        var index = state.State.Operations.FindIndex(operation =>
            string.Equals(operation.OperationId, operationId, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException($"Update operation '{operationId}' does not exist.");

        var operation = state.State.Operations[index];
        var key = string.Join('\u001f', ownerKind, ownerId, workId, taskRunId ?? string.Empty);
        var workIndex = operation.AffectedWorks.ToList().FindIndex(work => work.Key == key);
        if (workIndex < 0)
            throw new InvalidOperationException($"Update operation '{operationId}' does not name work '{key}'.");

        var works = operation.AffectedWorks.ToArray();
        var currentStatus = works[workIndex].Status;
        var nextStatus = MoreComplete(currentStatus, status);
        if (nextStatus == currentStatus)
            return operation;

        works[workIndex] = works[workIndex] with { Status = nextStatus };
        var nextOperation = operation with
        {
            AffectedWorks = works,
        };
        state.State.Operations[index] = nextOperation;
        await state.WriteStateAsync();
        return nextOperation;
    }

    private async Task LoadAsync()
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        state.State ??= new RunnerUpdateOperationState();
    }

    private void ValidateCandidate(RunnerUpdateOperation candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.RunnerId, this.GetPrimaryKeyString(), StringComparison.Ordinal))
            throw new InvalidOperationException("The update operation key does not match its Runner identity.");
        if (string.IsNullOrWhiteSpace(candidate.OperationId))
            throw new ArgumentException("An update operation requires a stable identity.", nameof(candidate));
        if (candidate.AffectedWorks is null)
            throw new ArgumentException("An update operation requires an affected-work inventory.", nameof(candidate));
    }

    private static bool IsValidWork(RunnerUpdateWork work) =>
        !string.IsNullOrWhiteSpace(work.OwnerKind)
        && !string.IsNullOrWhiteSpace(work.OwnerId)
        && !string.IsNullOrWhiteSpace(work.WorkId)
        && !string.IsNullOrWhiteSpace(work.WorkType);

    private static RunnerUpdateWorkStatus MoreComplete(
        RunnerUpdateWorkStatus current,
        RunnerUpdateWorkStatus requested) =>
        current == RunnerUpdateWorkStatus.Settled || requested == RunnerUpdateWorkStatus.Settled
            ? RunnerUpdateWorkStatus.Settled
            : current == RunnerUpdateWorkStatus.Marked || requested == RunnerUpdateWorkStatus.Marked
                ? RunnerUpdateWorkStatus.Marked
                : current == RunnerUpdateWorkStatus.AlreadyEnded || requested == RunnerUpdateWorkStatus.AlreadyEnded
                    ? RunnerUpdateWorkStatus.AlreadyEnded
                    : RunnerUpdateWorkStatus.Pending;
}
