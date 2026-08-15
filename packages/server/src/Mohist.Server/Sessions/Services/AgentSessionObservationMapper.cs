using Mohist.Server.Contracts;
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
            input.Provenance,
            StartupContextObservation(input.StartupContext))).ToArray();

    private static AgentStartupContextObservationDto? StartupContextObservation(AgentStartupContext? context) =>
        context is null
            ? null
            : new AgentStartupContextObservationDto(
                Source: context.Provenance.Source,
                Truncated: context.Provenance.Truncated,
                TruncationMarker: context.Provenance.TruncationMarker,
                OmittedOldestMessageCount: context.Provenance.OmittedOldestMessageCount);

    public static IReadOnlyList<AgentTurnObservationDto>? Turns(AgentSessionStatusSnapshot status) =>
        status.Turns?.Select(turn => new AgentTurnObservationDto(
            turn.Id,
            turn.Sequence,
            turn.InputIds,
            TurnStatus(turn.Status, turn.Interruption),
            turn.Result is null
                ? null
                : new AgentTurnResultObservationDto(
                    turn.Result.Message,
                    turn.Result.Output,
                    turn.Result.FailureReason,
                    turn.Result.FailureCategory,
                    turn.Result.ExitCode),
            ToDto(turn.Interruption))).ToArray();

    public static string InputAcceptance(AgentSessionInputAcceptance acceptance) => acceptance switch
    {
        AgentSessionInputAcceptance.Accepted => "accepted",
        AgentSessionInputAcceptance.Pending => "pending",
        AgentSessionInputAcceptance.Rejected => "rejected",
        _ => "unknown",
    };

    public static string TurnStatus(
        AgentTurnStatus status,
        AgentWorkInterruptionTransition? interruption = null) =>
        interruption is not null && AgentWorkInterruptionStates.IsKnown(interruption.State)
            ? interruption.State
            : status switch
            {
                AgentTurnStatus.Queued => "queued",
                AgentTurnStatus.Executing => "executing",
                AgentTurnStatus.Completed => "completed",
                AgentTurnStatus.Failed => "failed",
                AgentTurnStatus.Unknown => "unknown",
                AgentTurnStatus.Cancelled => "cancelled",
                _ => "unknown",
            };

    public static AgentWorkInterruptionTransitionDto? ToDto(
        AgentWorkInterruptionTransition? transition) =>
        transition is null
            ? null
            : new AgentWorkInterruptionTransitionDto(
                transition.State,
                transition.UpdateOperationId,
                transition.WorkId,
                transition.TaskRunId,
                transition.RecoveryGeneration,
                transition.OriginalTurnId,
                transition.ReplacementTurnId,
                AgentWorkInterruptionProjection.SanitizeStopFailure(transition.StopFailure),
                transition.ExpectedRecoveryPath,
                transition.RecordedAt.ToString("o"));

    public static IReadOnlyList<AgentWorkInterruptionTransitionDto>? History(
        AgentSessionStatusSnapshot status) =>
        status.InterruptionHistory is not { Count: > 0 } history
            ? null
            : history.Select(ToDto).Where(item => item is not null).Cast<AgentWorkInterruptionTransitionDto>().ToArray();

    public static AgentWorkInterruptionTransitionDto? Current(
        AgentSessionStatusSnapshot status) =>
        ToDto(AgentWorkInterruptionProjection.Latest(status.InterruptionHistory));
}
