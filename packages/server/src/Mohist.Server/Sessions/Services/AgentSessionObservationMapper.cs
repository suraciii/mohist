using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class AgentSessionObservationMapper
{
    public static IReadOnlyList<AgentSessionInputObservationDto>? Inputs(AgentSessionStatusSnapshot status) =>
        status.Inputs?.Select(input => new AgentSessionInputObservationDto(
            input.Id,
            input.Sequence,
            input.Source,
            input.Acceptance switch
            {
                AgentSessionInputAcceptance.Accepted => "accepted",
                AgentSessionInputAcceptance.Pending => "pending",
                AgentSessionInputAcceptance.Rejected => "rejected",
                _ => "unknown",
            })).ToArray();

    public static IReadOnlyList<AgentTurnObservationDto>? Turns(AgentSessionStatusSnapshot status) =>
        status.Turns?.Select(turn => new AgentTurnObservationDto(
            turn.Id,
            turn.Sequence,
            turn.InputIds,
            turn.Status switch
            {
                AgentTurnStatus.Queued => "queued",
                AgentTurnStatus.Executing => "executing",
                AgentTurnStatus.Completed => "completed",
                AgentTurnStatus.Failed => "failed",
                AgentTurnStatus.Unknown => "unknown",
                AgentTurnStatus.Cancelled => "cancelled",
                _ => "unknown",
            })).ToArray();
}
