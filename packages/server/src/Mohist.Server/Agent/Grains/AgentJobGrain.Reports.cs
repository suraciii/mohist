using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    public async Task<AgentJobReportResult> ReportResultAsync(string runnerId, string workId, WorkResult result)
    {
        await HydrateAsync();

        var fingerprint = WorkResultFingerprint.For(result);
        if (await FailRecoveringJobIfDueAsync())
            return new AgentJobReportResult(WorkReportVerdict.Refused, "refused");

        if (IsTerminal)
        {
            var exactReplay = string.Equals(State.AcceptedReportRunnerId, runnerId, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportWorkId, workId, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportFingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportAgentSessionId, result.AgentSessionId, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportAgentTurnId, result.AgentTurnId, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportRuntime, result.Runtime, StringComparison.Ordinal)
                && string.Equals(State.AcceptedReportRuntimeSessionId, result.RuntimeSessionId, StringComparison.Ordinal);
            _log.LogDebug(
                "AgentJob {Id} arbitrated terminal report from {Runner} for {Work}: {Verdict}",
                Key, runnerId, workId, exactReplay ? WorkReportVerdict.Accepted : WorkReportVerdict.Refused);
            if (State.PendingSessionClose is not null)
                await DeliverTerminalToSessionAsync(State.PendingSessionClose);
            if (State.PendingTerminalDeliveryEvent is not null)
                await EmitTerminalDeliveryEventAsync(State.PendingTerminalDeliveryEvent);
            if (State.PendingSubagentTerminalEvent is not null)
                await EmitSubagentTerminalEventAsync(State.PendingSubagentTerminalEvent);
            return new AgentJobReportResult(
                exactReplay ? WorkReportVerdict.Accepted : WorkReportVerdict.Refused,
                exactReplay ? null : "refused");
        }

        if (State.Status is not (AgentJobStatus.Running or AgentJobStatus.Unknown))
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: unexpected status {Status}",
                Key, runnerId, workId, State.Status);
            return new AgentJobReportResult(WorkReportVerdict.Refused, "not-running");
        }

        if (!string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, workId, StringComparison.Ordinal))
        {
            _log.LogWarning(
                "AgentJob {Id} rejecting report from {Runner} for {Work}: expected {ExpectedRunner}/{ExpectedWork}",
                Key, runnerId, workId, State.RunnerId, State.WorkId);
            return new AgentJobReportResult(WorkReportVerdict.Refused, "runner-or-work-mismatch");
        }
        if (!MatchesCurrentExecutionBinding(result))
            return new AgentJobReportResult(WorkReportVerdict.Refused, "execution-binding-mismatch");
        if (IsManagerCredentialExpired(result)) return await ReportManagerCredentialExpiredAsync(result);
        if (string.Equals(result.Status, "unknown", StringComparison.OrdinalIgnoreCase)) return await ReportUnknownResultAsync(result);
        var isSuccess = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "pass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "success", StringComparison.OrdinalIgnoreCase);

        var failureReason = isSuccess
            ? null
            : (string.IsNullOrWhiteSpace(result.Message) ? result.Status : result.Message);

        var failureCategory = isSuccess
            ? null
            : FailureCategoryFromOutput(result.Output)
                ?? FailureCategoryFromErrorCode(result.ErrorCode)
                ?? FailureCategoryFromStatus(result.Status);

        State.AcceptedReportRunnerId = runnerId;
        State.AcceptedReportWorkId = workId;
        State.AcceptedReportFingerprint = fingerprint;
        State.AcceptedReportAgentSessionId = result.AgentSessionId;
        State.AcceptedReportAgentTurnId = result.AgentTurnId;
        State.AcceptedReportRuntime = result.Runtime;
        State.AcceptedReportRuntimeSessionId = result.RuntimeSessionId;
        _reportPersistenceFailures.BeforePersist(Key, workId);
        await EnterTerminalStateAsync(
            isSuccess ? AgentJobStatus.Completed : AgentJobStatus.Failed,
            isSuccess ? (int?)0 : (result.ExitCode ?? 1),
            failureReason,
            failureCategory,
            failureReason,
            result.Message,
            result.Output?.ValueKind == System.Text.Json.JsonValueKind.Object || result.Output?.ValueKind == System.Text.Json.JsonValueKind.Array
                ? result.Output.Value.GetRawText()
                : null,
            result.ArtifactUploadIds,
            result.ExitCode);

        return new AgentJobReportResult(WorkReportVerdict.Accepted);
    }

    private bool MatchesCurrentExecutionBinding(WorkResult result)
    {
        var carriesBinding = result.AgentSessionId is not null
            || result.AgentTurnId is not null
            || result.Runtime is not null
            || result.RuntimeSessionId is not null;
        if (!carriesBinding)
            return true;

        return !string.IsNullOrEmpty(result.AgentSessionId)
            && !string.IsNullOrEmpty(result.AgentTurnId)
            && !string.IsNullOrEmpty(result.Runtime)
            && !string.IsNullOrEmpty(result.RuntimeSessionId)
            && string.Equals(State.Input?.AgentSessionId, result.AgentSessionId, StringComparison.Ordinal)
            && string.Equals(State.Input?.InitialTurnId, result.AgentTurnId, StringComparison.Ordinal)
            && string.Equals(State.Input?.Runtime, result.Runtime, StringComparison.Ordinal)
            && string.Equals(State.RuntimeSessionId, result.RuntimeSessionId, StringComparison.Ordinal);
    }

    public async Task<WorkReportVerdict> FailRunnerLostAsync(
        string runnerId,
        string workId,
        string processGeneration)
    {
        await HydrateAsync();

        if (IsTerminal)
            return WorkReportVerdict.Refused;
        if (State.Status != AgentJobStatus.Running
            || !string.Equals(State.RunnerId, runnerId, StringComparison.Ordinal)
            || !string.Equals(State.WorkId, workId, StringComparison.Ordinal)
            || !string.Equals(_ledger?.ClaimedProcessGeneration, processGeneration, StringComparison.Ordinal))
        {
            return WorkReportVerdict.Refused;
        }

        await FailAsync(AgentJobFailureReasons.RunnerLost);
        return WorkReportVerdict.Accepted;
    }

 }
