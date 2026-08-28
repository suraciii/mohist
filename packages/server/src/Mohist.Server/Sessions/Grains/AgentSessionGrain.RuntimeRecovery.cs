using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private void EnsureSessionRecoverableBeforeInputSubmission(AgentSession session, string? expectedQueuedTurnId)
    {
        if (string.IsNullOrWhiteSpace(expectedQueuedTurnId))
        {
            EnsureSessionIdleForRecovery(session);
            return;
        }

        var nonTerminalTurns = (session.Status.Turns ?? [])
            .Where(turn => turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing)
            .ToArray();
        var pending = GetPendingFollowups(session);
        var matchingLease = pending.SingleOrDefault(lease =>
            string.Equals(lease.TurnId, expectedQueuedTurnId, StringComparison.Ordinal));
        var isSealedQueuedDispatch = nonTerminalTurns.Length == 1
            && nonTerminalTurns[0].Status == AgentTurnStatus.Queued
            && string.Equals(nonTerminalTurns[0].Id, expectedQueuedTurnId, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(nonTerminalTurns[0].JobId)
            && pending.Count == 1
            && matchingLease is { Dispatching: true, PayloadSealed: true }
            && session.Status.PendingStop is not { IsActive: true }
            && session.Status.Activity != AgentSessionActivity.Unknown;
        if (!isSealedQueuedDispatch)
        {
            throw new InvalidOperationException(
                $"AgentSession {session.Id} cannot recover its Runtime binding for queued Turn "
                + $"'{expectedQueuedTurnId}' because the pre-submission dispatch fence does not match.");
        }
    }
}
