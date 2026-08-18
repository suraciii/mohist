using Mohist.Server.Infrastructure;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private PendingSessionClose BuildPendingSessionClose(
        AgentJobStatus terminalStatus,
        int? exitCode,
        string? failureReason,
        string? failureCategory,
        string? pendingReason)
    {
        if (State.PendingSessionClose is { } existing)
            return existing;

        var statusText = terminalStatus switch
        {
            AgentJobStatus.Completed => "completed",
            AgentJobStatus.Cancelled => "cancelled",
            _ => "failed",
        };
        return new PendingSessionClose(
            DeliveryId: AgentJobSessionDeliveryIds.TerminalDeliveryId(Key),
            Status: statusText,
            ExitCode: exitCode,
            FailureReason: failureReason ?? pendingReason,
            FailureCategory: failureCategory,
            RecordedAt: _timeProvider.GetUtcNow());
    }

    private async Task DeliverTerminalToSessionAsync(PendingSessionClose pending)
    {
        var sessionId = State.Input?.AgentSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await ClearPendingSessionCloseAndMaybeReminderAsync();
            return;
        }

        try
        {
            var grain = GrainFactory.GetGrain<IAgentSessionGrain>(sessionId);
            var payloadJson = JSON.Serialize(new Dictionary<string, object?>
            {
                ["status"] = pending.Status,
                ["exitCode"] = pending.ExitCode,
                ["failureReason"] = pending.FailureReason,
                ["failureCategory"] = pending.FailureCategory,
                ["recordedAt"] = pending.RecordedAt.ToString("o"),
                ["agentJobId"] = Key,
                ["deliveryId"] = pending.DeliveryId,
            });
            await grain.AppendTerminalCloseAsync(new AppendTerminalCloseCommand(
                SessionId: sessionId,
                DeliveryId: pending.DeliveryId,
                Status: pending.Status,
                ExitCode: pending.ExitCode,
                FailureReason: pending.FailureReason,
                FailureCategory: pending.FailureCategory,
                RecordedAt: pending.RecordedAt,
                PayloadJson: payloadJson,
                RuntimeSessionId: State.RuntimeSessionId));

            await ClearPendingSessionCloseAndMaybeReminderAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentJob {Id} terminal delivery to session {SessionId} failed; deliveryId={DeliveryId} retained for retry",
                Key, sessionId, pending.DeliveryId);
        }
    }

    private async Task ClearPendingSessionCloseAndMaybeReminderAsync()
    {
        State.PendingSessionClose = null;
        await PersistAsync();
        if (State.PendingFailureEvent is not null
            || State.PendingTerminalDeliveryEvent is not null
            || State.PendingSubagentTerminalEvent is not null)
            return;
        try
        {
            var reminder = await this.GetReminder(RecoveryReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "AgentJob {Id} could not unregister recovery reminder; orphan tick will self-clean",
                Key);
        }
    }
}
