using Mohist.Server.Infrastructure.Capacity;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

/// <summary>
/// The Session owner's side of derived Agent capacity: the queued follow-up
/// Turn claims its slot inside its own serialized grain turn, and one durable
/// reminder keeps current queued work reachable across activation loss and
/// crash-before-dispatch. The claim API, the occupancy facts, and the
/// admission rules live in the capacity store; this file only sequences them
/// around the single-writer turn.
/// </summary>
public sealed partial class AgentSessionGrain
{
    internal const string FollowupQueueReminderName = "followup-queue";
    internal static readonly TimeSpan FollowupQueueReminderPeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Claims the queued follow-up Turn against the authoritative capacity
    /// store. Pending state, events, and transcript obligations are flushed
    /// first, so a flush that does not succeed writes no claim. The claim
    /// token is the actual persisted State document, and the complete
    /// committed Session the store returns is installed before any later
    /// save, timer, or dispatch. A document conflict reloads the fresh owner
    /// and retries once; anything else fails closed without a claim. An
    /// exception is an uncertain commit: the activation is quarantined before
    /// another write or effect, so a stale document can never overwrite the
    /// committed claim.
    /// </summary>
    private async Task<bool> ClaimQueuedFollowupTurnAsync(AgentSession session, string turnId)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await FlushAsync(CancellationToken.None))
                return false;
            var token = await _stateStore.ReadStateJsonAsync(SessionId, CancellationToken.None);
            if (string.IsNullOrEmpty(token))
                return false;

            AgentTurnCapacityClaimResult claim;
            try
            {
                claim = await _capacityStore.ClaimTurnAsync(SessionId, token, turnId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex,
                    "AgentSession {SessionId} capacity claim for turn {TurnId} ended in an uncertain commit",
                    SessionId,
                    turnId);
                QuarantineActivation();
                throw;
            }

            switch (claim.Disposition)
            {
                case AgentCapacityClaimDisposition.Claimed:
                case AgentCapacityClaimDisposition.AlreadyClaimed:
                    if (claim.Session is null)
                    {
                        QuarantineActivation();
                        return false;
                    }
                    _session = claim.Session;
                    return true;
                case AgentCapacityClaimDisposition.Conflict:
                    // The persisted document moved under this turn. Reload the
                    // fresh owner and retry once from it; a second conflict
                    // leaves the queue waiting for the next wake instead of
                    // writing from stale state.
                    var reloaded = await _stateStore.LoadAsync(SessionId);
                    if (reloaded is null)
                        return false;
                    _session = reloaded;
                    session = reloaded;
                    continue;
                default:
                    // Capacity full, out of order, not eligible, or incomplete
                    // owner evidence: the accepted Turn keeps its identity and
                    // stays queued for the next wake. Never a false full
                    // conclusion and never a dispatch without a claim.
                    return false;
            }
        }

        return false;
    }

    private void QuarantineActivation()
    {
        _sessionReloadRequired = true;
        DeactivateOnIdle();
    }

    /// <summary>
    /// Current, nonsuperseded, ordinary queued follow-up work in this Session,
    /// claimed or not. A claimed head keeps its slot and still has to be
    /// delivered, so it stays covered by the reminder.
    /// </summary>
    private static bool HasQueuedFollowupWork(AgentSession session)
    {
        var turns = session.Status.Turns ?? [];
        foreach (var turn in turns)
        {
            if (turn.SupersededAt is not null
                || turn.ContextGeneration != session.Status.ContextGeneration
                || !string.IsNullOrWhiteSpace(turn.JobId)
                || turn.Status != AgentTurnStatus.Queued)
                continue;
            return true;
        }

        return false;
    }

    private async Task EnsureFollowupQueueReminderAsync()
    {
        if (_session is { } session && HasQueuedFollowupWork(session))
        {
            await this.RegisterOrUpdateReminder(
                FollowupQueueReminderName,
                FollowupQueueReminderPeriod,
                FollowupQueueReminderPeriod);
            return;
        }

        try
        {
            var reminder = await this.GetReminder(FollowupQueueReminderName);
            if (reminder is not null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An orphan reminder without queued work is harmless: its wake
            // finds nothing and drops it.
        }
    }

    /// <summary>
    /// Wakes the established dispatcher for current queued work. The reminder
    /// itself never dispatches and never grants a permit; it only makes the
    /// queue reachable again after an activation loss or a claim that could
    /// not yet be admitted.
    /// </summary>
    private async Task WakeQueuedFollowupAsync()
    {
        if (_session is null)
        {
            await EnsureFollowupQueueReminderAsync();
            return;
        }

        if (!HasQueuedFollowupWork(_session))
        {
            await EnsureFollowupQueueReminderAsync();
            return;
        }

        _followupDispatchScheduler?.Schedule(
            _session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId) ?? string.Empty,
            SessionId);
        await EnsureFollowupQueueReminderAsync();
    }
}
