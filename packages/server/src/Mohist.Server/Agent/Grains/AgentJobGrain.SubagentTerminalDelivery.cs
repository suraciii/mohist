using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Agent.Grains;

public sealed partial class AgentJobGrain
{
    private void StageSubagentTerminalEvent(AgentJobStatus status)
    {
        if (status is not (AgentJobStatus.Completed or AgentJobStatus.Failed or AgentJobStatus.Cancelled or AgentJobStatus.Unknown)
            || State.Input?.SpawnOrigin is null
            || State.LaunchVisibility != AgentLaunchVisibility.Visible
            || State.PendingSubagentTerminalEvent is not null)
            return;

        // Only an accepted (visible) delegation owes a terminal callback;
        // a provisional or rejected launch was never attached to a parent
        // SessionParentLink, so a cancelled job here must stay silent.
        State.PendingSubagentTerminalEvent = new PendingSubagentTerminalEvent(
            AgentJobSessionDeliveryIds.SubagentTerminalEventId(Key),
            State.Input.SpawnOrigin,
            status,
            $"agent-job:{Key}",
            _timeProvider.GetUtcNow());
    }

    private async Task EmitSubagentTerminalEventAsync(PendingSubagentTerminalEvent pending)
    {
        try
        {
            await _eventStore.AppendAsync(BuildSubagentTerminalEnvelope(pending), CancellationToken.None);
            _dispatchSignal.Wake();
            State.PendingSubagentTerminalEvent = null;
            await PersistAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AgentJob {Id} subagent terminal event is retained for retry", Key);
        }
    }

    internal CloudEvent BuildSubagentTerminalEnvelope(PendingSubagentTerminalEvent pending) =>
        AgentJobLineage.BuildSubagentTerminalEnvelope(Key, pending);
}
