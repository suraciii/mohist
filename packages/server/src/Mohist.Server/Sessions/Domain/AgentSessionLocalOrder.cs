namespace Mohist.Server.Sessions.Domain;

/// <summary>
/// The one Session-local delivery-order rule shared by the follow-up
/// dispatcher, the initial-input admission boundary, and the derived capacity
/// store. It answers a single question over the aggregate: which Turn may
/// advance next in this Session. Claim stamps and dispatch leases are owner
/// facts that stay with each consumer; this rule sees only the Session.
/// </summary>
internal static class AgentSessionLocalOrder
{
    /// <summary>
    /// Earliest current, nonsuperseded queued Turn — Job-owned or ordinary —
    /// by Sequence then ordinal TurnId, provided the Session's fences allow
    /// any advance: pending Stop or Reset, a current Executing Turn, confirmed
    /// Runtime ownership of an Executing or Unknown Turn, and an unresolved
    /// Unknown all hold the Session back. A merely later queued Turn never
    /// vetoes the earlier head. <paramref name="allowBesideUnknown"/> is the
    /// caller's own narrow inspection exception to the Unknown hold; it is
    /// caller-supplied so this rule stays free of provider and catalog
    /// identities.
    /// </summary>
    internal static AgentTurnRecord? FirstDeliverableTurn(
        AgentSession session,
        Func<AgentSession, AgentTurnRecord, bool>? allowBesideUnknown = null)
    {
        var current = (session.Status.Turns ?? [])
            .Where(turn => turn.SupersededAt is null
                && turn.ContextGeneration == session.Status.ContextGeneration)
            .ToArray();

        if (session.Status.PendingStop is { IsActive: true }
            || session.Status.PendingReset is { Outcome: null, SupersededAt: null }
            || current.Any(turn => turn.Status == AgentTurnStatus.Executing)
            || HasConfirmedOwner(session, current))
            return null;

        var head = current
            .Where(turn => turn.Status == AgentTurnStatus.Queued)
            .OrderBy(turn => turn.Sequence)
            .ThenBy(turn => turn.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (head is null)
            return null;

        // An unresolved Unknown keeps exclusive hold of the Session: an
        // ordinary queued follow-up must not turn a lost response into
        // permission for another effect.
        if (current.Any(turn => turn.Status == AgentTurnStatus.Unknown)
            && allowBesideUnknown?.Invoke(session, head) != true)
            return null;
        return head;
    }

    internal static bool IsDeliverableHead(
        AgentSession session,
        string turnId,
        Func<AgentSession, AgentTurnRecord, bool>? allowBesideUnknown = null) =>
        FirstDeliverableTurn(session, allowBesideUnknown) is { } head
            && string.Equals(head.Id, turnId, StringComparison.Ordinal);

    private static bool HasConfirmedOwner(
        AgentSession session,
        IReadOnlyCollection<AgentTurnRecord> current) =>
        session.Status.ConfirmedExecutionOwnership is { } ownership
            && ownership.ContextGeneration == session.Status.ContextGeneration
            && current.Any(turn => ownership.TurnIds.Contains(turn.Id, StringComparer.Ordinal)
                && turn.Status is AgentTurnStatus.Executing or AgentTurnStatus.Unknown);
}
