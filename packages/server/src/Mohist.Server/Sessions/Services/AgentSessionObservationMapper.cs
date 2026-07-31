using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

internal static class AgentSessionObservationMapper
{
    public static IReadOnlyList<AgentSessionInputObservationDto>? Inputs(AgentSessionStatusSnapshot status) =>
        status.Inputs?.Select(input => new AgentSessionInputObservationDto(
            input.Id,
            input.Sequence,
            input.Source,
            InputAcceptance(input.Acceptance),
            input.Attachments?.Select(attachment => new AgentSessionInputAttachmentObservationDto(
                attachment.Id,
                attachment.OriginalFileName,
                attachment.ContentType,
                attachment.Size,
                attachment.Source,
                attachment.Availability)).ToArray(),
            input.Provenance)).ToArray();

    public static IReadOnlyList<AgentTurnObservationDto>? Turns(AgentSessionStatusSnapshot status) =>
        status.Turns?.Select(turn => new AgentTurnObservationDto(
            turn.Id,
            turn.Sequence,
            turn.InputIds,
            TurnStatus(turn.Status))).ToArray();

    public static string InputAcceptance(AgentSessionInputAcceptance acceptance) => acceptance switch
    {
        AgentSessionInputAcceptance.Accepted => "accepted",
        AgentSessionInputAcceptance.Pending => "pending",
        AgentSessionInputAcceptance.Rejected => "rejected",
        _ => "unknown",
    };

    public static string TurnStatus(AgentTurnStatus status) => status switch
    {
        AgentTurnStatus.Queued => "queued",
        AgentTurnStatus.Executing => "executing",
        AgentTurnStatus.Completed => "completed",
        AgentTurnStatus.Failed => "failed",
        AgentTurnStatus.Unknown => "unknown",
        AgentTurnStatus.Cancelled => "cancelled",
        _ => "unknown",
    };
}
