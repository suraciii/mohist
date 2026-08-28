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

    private async Task<bool> FailReadyTimeoutIfExceededAsync()
    {
        var bound = _options.ResolveBackoffSchedule().TotalBound;
        if (State.Status != AgentJobStatus.Pending
            || string.IsNullOrWhiteSpace(State.RunnerId)
            || State.ReadySince is not { } readySince
            || _timeProvider.GetUtcNow() < readySince + bound)
        {
            return false;
        }

        _log.LogWarning(
            "AgentJob {Id} was not claimed by runner {Runner} within {Timeout}",
            Key, State.RunnerId, bound);
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
