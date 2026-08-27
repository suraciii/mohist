namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private void StageTerminalDeliveryEvent(
        AgentJobStatus status,
        string? message,
        string? output,
        string? failureReason,
        string? failureCategory,
        string[]? artifactUploadIds,
        int? exitCode)
    {
        var origin = State.ManualPlan?.ConnectionOrigin;
        if (origin is null)
            return;

        if (State.PendingTerminalDeliveryEvent is { } pending
            && (status == AgentJobStatus.Unknown || pending.Status != AgentJobStatus.Unknown))
            return;

        var eventId = status == AgentJobStatus.Unknown
            ? AgentJobSessionDeliveryIds.UnknownTerminalDeliveryEventId(Key)
            : AgentJobSessionDeliveryIds.TerminalDeliveryEventId(Key);
        State.PendingTerminalDeliveryEvent = new PendingTerminalDeliveryEvent(
            eventId,
            origin,
            status,
            message,
            failureReason,
            failureCategory,
            artifactUploadIds?.Length ?? 0,
            exitCode,
            _timeProvider.GetUtcNow(),
            output);
    }

    private async Task EmitTerminalDeliveryEventAsync(PendingTerminalDeliveryEvent pending)
    {
        try
        {
            var envelope = BuildTerminalDeliveryEnvelope(pending);
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            _dispatchSignal.Wake();
            State.PendingTerminalDeliveryEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} terminal delivery event is retained for retry", Key);
        }
    }
}
