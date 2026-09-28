using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    private async Task TryEmitFollowupTerminalDeliveriesAsync(
        AgentSession session,
        Dictionary<string, AgentTurnStatus> before)
    {
        var turns = session.Status.Turns;
        if (turns is null || turns.Count == 0) return;
        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.JobId)) continue;
            before.TryGetValue(turn.Id, out var prior);
            if (IsTerminalTurn(prior) || !IsTerminalTurn(turn.Status)) continue;
            await TryEmitFollowupDeliveryAsync(session, turn);
        }
    }

    private static bool IsTerminalTurn(AgentTurnStatus status) =>
        status is not AgentTurnStatus.Queued and not AgentTurnStatus.Executing;

    private async Task SettlePendingFollowupsForUnboundLaunchAsync(
        AgentSession session,
        AgentTurnRecord? launchTurn,
        AgentTurnStatus status,
        AgentTurnResult? result)
    {
        if (launchTurn is null
            || string.IsNullOrWhiteSpace(launchTurn.JobId)
            || status is not (AgentTurnStatus.Failed or AgentTurnStatus.Cancelled)
            || !string.IsNullOrWhiteSpace(session.Status.AgentRuntimeSessionId)
            || AgentSessionRetryPolicy.IsRetryable(result?.FailureCategory))
            return;

        var turns = session.Status.Turns ?? [];
        var candidates = (session.Status.PendingFollowups ?? [])
            .Where(lease => lease.Accepted && !lease.Dispatching && !string.IsNullOrWhiteSpace(lease.TurnId))
            .Where(lease => turns.Any(turn =>
                string.Equals(turn.Id, lease.TurnId, StringComparison.Ordinal)
                && turn.Status == AgentTurnStatus.Queued
                && string.IsNullOrWhiteSpace(turn.JobId)))
            .ToArray();
        if (candidates.Length == 0)
            return;

        var now = Now();
        var before = session.Status;
        var rejection = new AgentTurnResult(
            Message: SlackFollowupRejection.OwnerTerminalText,
            FailureReason: SlackFollowupRejection.OwnerTerminalText,
            FailureCategory: SlackFollowupRejection.RuntimeSessionMissingCategory);
        foreach (var lease in candidates)
            _ = session.MarkFollowupTurnTerminal(lease.OperationId, AgentTurnStatus.Failed, rejection, now);

        if (ReferenceEquals(before, session.Status))
            return;

        await CommitAsync(session, []);
        await PublishCanonicalRefreshAsync(session);
        foreach (var lease in candidates)
        {
            await ReleaseFollowupConcurrencyPermitAsync(
                session,
                lease.ConcurrencyToken,
                lease.ConcurrencyAgentId,
                lease.ConcurrencyPermitId,
                lease.ConcurrencyGeneration,
                lease.ConcurrencyWaiterId);
        }

        var settledIds = candidates
            .Select(lease => lease.TurnId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var turn in (session.Status.Turns ?? []).Where(turn => settledIds.Contains(turn.Id)))
            await TryEmitFollowupDeliveryAsync(session, turn);
    }
}
