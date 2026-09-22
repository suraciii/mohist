using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

public partial class AgentSessionQuerier
{
    private static IEnumerable<AgentTurnRecord> CurrentTurns(AgentSession session) =>
        (session.Status.Turns ?? []).Where(turn =>
            turn.ContextGeneration == session.Status.ContextGeneration && turn.SupersededAt is null);

    private static string? CurrentTurnId(AgentSession session)
    {
        var turns = CurrentTurns(session).ToArray();
        return turns.FirstOrDefault(turn => turn.Status == AgentTurnStatus.Executing)?.Id
            ?? ConfirmedUnknownOwner(session, turns)?.Id
            ?? turns.LastOrDefault(turn => turn.Status == AgentTurnStatus.Queued)?.Id
            ?? (session.Status.Activity == AgentSessionActivity.Unknown
                ? turns.LastOrDefault(turn => turn.Status == AgentTurnStatus.Unknown)?.Id
                : null);
    }

    private static AgentTurnRecord? ConfirmedUnknownOwner(AgentSession session, IEnumerable<AgentTurnRecord> turns) =>
        session.Status.ConfirmedExecutionOwnership is { } ownership
            && ownership.ContextGeneration == session.Status.ContextGeneration
            ? turns.FirstOrDefault(turn => turn.Status == AgentTurnStatus.Unknown
                && ownership.TurnIds.Contains(turn.Id, StringComparer.Ordinal))
            : null;

    private static bool IsRecoveryAvailable(AgentSession session) =>
        session.Status.Activity == AgentSessionActivity.Idle
        && session.Status.PendingReset is not { Outcome: null, SupersededAt: null }
        && session.Status.PendingStop is not { IsActive: true }
        && session.Status.PendingFollowup is null
        && (session.Status.PendingFollowups is null || session.Status.PendingFollowups.Count == 0)
        && !CurrentTurns(session).Any(turn => turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
        && ConfirmedUnknownOwner(session, CurrentTurns(session)) is null;
}
