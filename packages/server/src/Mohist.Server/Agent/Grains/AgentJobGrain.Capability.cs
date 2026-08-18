using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.AgentJobs;
using Mohist.Server.Infrastructure.Orleans;
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

    public Task<ClaimResult?> ClaimNextAsync(string runnerId) =>
        ClaimNextAsync(runnerId, null);

    public async Task<ClaimResult?> ClaimNextAsync(
        string runnerId,
        CapabilityClaimExpectation? expectation = null)
    {
        await HydrateAsync();

        if (string.IsNullOrWhiteSpace(runnerId))
            return null;

        // Validate the assignment under the row's revision. A concurrent
        // admission can move the AssignedRunnerId; in that case the
        // claim is skipped (the caller observes the new assignee on a
        // later poll).
        if (!string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal))
            return null;
        if (State.Status != AgentJobStatus.Pending)
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
            ? await _jobStore.ClaimAsync(Key, runnerId, _timeProvider.GetUtcNow())
            : await _jobStore.ClaimAsync(
                Key,
                runnerId,
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
        if (State.ConcurrencyPermitHeld
            && State.ConcurrencyPermitId is not null
            && State.ConcurrencyDispatchId is not null
            && State.Input?.ProjectId is { } projectId
            && State.Input.AgentId is { } agentId)
        {
            await _grains.GetGrain<IAgentConcurrencyGrain>(GrainKey.Agent(projectId, agentId))
                .MarkExecutingAsync(
                    projectId,
                    agentId,
                    State.ConcurrencyPermitToken!,
                    State.ConcurrencyPermitId,
                    State.ConcurrencyDispatchId);
            State.ConcurrencyGateStatus = AgentConcurrencyPermitStatus.Executing;
            State.WaitingReason = null;
            await PersistAsync();
        }

        return new ClaimResult(Key, runnerId, State.WorkId!, dispatch);
    }
}
