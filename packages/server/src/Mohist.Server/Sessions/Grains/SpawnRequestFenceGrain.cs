using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public interface ISpawnRequestFenceGrain : IGrainWithStringKey
{
    Task<SpawnRequestFence?> GetAsync();
    Task<SpawnRequestFence> StartAsync(SpawnRequestFence candidate);
    Task<SpawnRequestFence> SetOutcomeAsync(
        SpawnRequestFenceOutcome outcome,
        string? rejectionReason = null);
}

public sealed class SpawnRequestFenceGrain(
    [PersistentState("spawn-request-fence")] IPersistentState<SpawnRequestFenceState> state)
    : Grain, ISpawnRequestFenceGrain
{
    public async Task<SpawnRequestFence?> GetAsync()
    {
        if (!state.RecordExists)
            await state.ReadStateAsync();
        return state.State.Fence;
    }

    public async Task<SpawnRequestFence> StartAsync(SpawnRequestFence candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var existing = await GetAsync();
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, candidate.RequestFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The spawn request fingerprint conflicts with the existing idempotency key.");
            return existing;
        }

        state.State.Fence = candidate;
        await state.WriteStateAsync();
        return candidate;
    }

    public async Task<SpawnRequestFence> SetOutcomeAsync(
        SpawnRequestFenceOutcome outcome,
        string? rejectionReason = null)
    {
        var current = await GetAsync()
            ?? throw new InvalidOperationException("The spawn request fence has not been started.");
        var next = current with { Outcome = outcome, PreplanRejectionReason = rejectionReason };
        state.State.Fence = next;
        await state.WriteStateAsync();
        return next;
    }
}

[GenerateSerializer]
public sealed class SpawnRequestFenceState
{
    [Id(0)] public SpawnRequestFence? Fence { get; set; }
}
