using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private bool MatchesCapabilityExpectation(
        CapabilityClaimExpectation expectation,
        WorkDispatch dispatch,
        string runnerId)
    {
        if (!string.Equals(expectation.OwnerKind, WorkDispatchOwnerKinds.AgentJob, StringComparison.Ordinal)
            || !string.Equals(expectation.OwnerId, Key, StringComparison.Ordinal)
            || !string.Equals(expectation.WorkId, State.WorkId, StringComparison.Ordinal)
            || !string.Equals(expectation.WorkId, dispatch.WorkId, StringComparison.Ordinal))
        {
            return false;
        }

        var definition = dispatch.AgentDefinition;
        return definition is not null
            && string.Equals(definition.Runtime, expectation.Runtime, StringComparison.Ordinal)
            && string.Equals(definition.Model, expectation.Model, StringComparison.Ordinal)
            && string.Equals(definition.ReasoningEffort, expectation.ReasoningEffort, StringComparison.Ordinal)
            && string.Equals(definition.Variant, expectation.Variant, StringComparison.Ordinal)
            && (expectation.CapabilityRevision is null || expectation.CapabilityRevision.Length > 0)
            && !string.IsNullOrWhiteSpace(runnerId);
    }

    public Task<ClaimResult?> ClaimNextAsync(string runnerId, string processGeneration) =>
        ClaimNextAsync(runnerId, processGeneration, null);

    public async Task<ClaimResult?> ClaimNextAsync(
        string runnerId,
        string processGeneration,
        CapabilityClaimExpectation? expectation = null)
    {
        await HydrateAsync();

        if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(processGeneration))
            return null;

        // Validate the assignment under the row's revision. A concurrent
        // admission can move the AssignedRunnerId; in that case the
        // claim is skipped (the caller observes the new assignee on a
        // later poll).
        if (!string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal))
            return null;
        if (State.Status != AgentJobStatus.Pending)
            return null;
        if (await FailReadyTimeoutIfExceededAsync())
            return null;
        if (string.IsNullOrWhiteSpace(State.WorkId))
            return null;

        var pendingDispatch = DeserializeDispatch(_ledger?.DispatchJson);
        if (pendingDispatch is null)
            throw new AgentJobLedgerReconstructionException(
                $"AgentJob '{Key}' claim has no parseable dispatch snapshot");

        if (State.Input?.ReasoningEffort is not null && expectation is null)
            return null;

        if (expectation is not null
            && !MatchesCapabilityExpectation(expectation, pendingDispatch, runnerId))
        {
            return null;
        }

        var claimDispatch = expectation is null
            ? pendingDispatch
            : pendingDispatch with { CapabilityClaim = expectation };

        var record = expectation is null
            ? await _jobStore.ClaimAsync(Key, runnerId, processGeneration, _timeProvider.GetUtcNow())
            : await _jobStore.ClaimAsync(
                Key,
                runnerId,
                processGeneration,
                _timeProvider.GetUtcNow(),
                expectation.WorkId,
                JsonSerializer.Serialize(claimDispatch, JSON.Options));
        _hydrated = false;
        await HydrateAsync();
        var dispatch = DeserializeDispatch(record.DispatchJson)
            ?? throw new AgentJobLedgerReconstructionException(
                $"AgentJob '{Key}' claim returned a row without a parseable dispatch snapshot");

        ArmJobTimeout();
        await SafeRunnerAcceptedAsync(runnerId, State.WorkId!);
        // The runner claim owns the transition to Running; once the poll
        // path accepted the work, this Job is no longer waiting.
        if (State.WaitingReason is not null)
        {
            State.WaitingReason = null;
            await PersistAsync();
        }

        return new ClaimResult(Key, runnerId, State.WorkId!, dispatch);
    }
}
