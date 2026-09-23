namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private async Task EvaluatePendingAsync()
    {
        if (await FailReadyTimeoutIfExceededAsync())
            return;

        if (string.IsNullOrWhiteSpace(State.RunnerId)
            || string.IsNullOrWhiteSpace(_ledger?.DispatchJson))
        {
            await TryAdmitAsync();
        }
    }

    /// <summary>
    /// The pending-work bound starts only at the Job's actual capacity
    /// claim: a claimed-but-undispatched launch — with or without a
    /// runner assignment — fails after the availability bound. A stale
    /// readiness timestamp without a claim is not this clock, and
    /// Running or Unknown work never settles by this bound; those states
    /// occupy their slot until their own terminal fact.
    /// </summary>
    private async Task<bool> FailReadyTimeoutIfExceededAsync()
    {
        var bound = _options.ResolveBackoffSchedule().TotalBound;
        if (State.Status != AgentJobStatus.Pending
            || State.CapacityClaimedAt is not { } claimedAt
            || _timeProvider.GetUtcNow() < claimedAt + bound)
        {
            return false;
        }

        _log.LogWarning(
            "AgentJob {Id} was not claimed by runner {Runner} within {Timeout}",
            Key, State.RunnerId ?? "<unassigned>", bound);
        await EnterTerminalStateAsync(
            AgentJobStatus.Failed,
            exitCode: 1,
            failureReason: AgentJobFailureReasons.RunnerUnavailable,
            failureCategory: AgentJobFailureReasons.RunnerUnavailable,
            pendingReason: AgentJobFailureReasons.RunnerUnavailable,
            message: AgentJobFailureReasons.RunnerUnavailable,
            output: null,
            artifactUploadIds: null,
            terminalExitCode: 1);
        return true;
    }
}
