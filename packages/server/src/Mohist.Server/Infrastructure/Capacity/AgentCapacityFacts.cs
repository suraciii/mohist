using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Infrastructure.Capacity;

internal static class AgentCapacityFacts
{
    internal static bool Occupies(AgentJobState job) =>
        job.TerminalAt is null
        && (job.Status is AgentJobStatus.Running or AgentJobStatus.Unknown
            || job.Status == AgentJobStatus.Pending && job.CapacityClaimedAt is not null);

    internal static bool Occupies(AgentSession session, AgentTurnRecord turn) =>
        string.IsNullOrWhiteSpace(turn.JobId)
        && turn.SupersededAt is null
        && turn.ContextGeneration == session.Status.ContextGeneration
        && (turn.Status is AgentTurnStatus.Executing or AgentTurnStatus.Unknown
            || turn.Status == AgentTurnStatus.Queued && turn.CapacityClaimedAt is not null);

    internal static bool IsEligible(AgentJobState job) =>
        job.Status == AgentJobStatus.Pending
        && job.LaunchVisibility == AgentLaunchVisibility.Visible
        && job.TerminalAt is null
        && job.CapacityClaimedAt is null
        && job.SubmittedAt is not null
        && !string.IsNullOrWhiteSpace(job.Input?.ProjectId)
        && !string.IsNullOrWhiteSpace(job.Input?.AgentId);

    internal static AgentTurnRecord? EligibleHead(AgentSession session)
    {
        // The Session's global-FIFO candidate is its local head only while
        // that head is ordinary and unclaimed: a Job-owned head competes
        // through its own Job entry, and a claimed head already holds its
        // slot without re-entering the queue.
        var head = AgentSessionLocalOrder.FirstDeliverableTurn(session, IsManagerRecovery);
        if (head is null
            || !string.IsNullOrWhiteSpace(head.JobId)
            || head.CapacityClaimedAt is not null)
            return null;

        var leases = session.Status.PendingFollowups
            ?? (session.Status.PendingFollowup is null ? [] : [session.Status.PendingFollowup]);
        var lease = leases.FirstOrDefault(candidate =>
            string.Equals(candidate.TurnId, head.Id, StringComparison.Ordinal));
        return lease is { Accepted: true, Dispatching: false } ? head : null;
    }

    internal static AgentCapacityQueueEntry JobEntry(string jobKey, AgentJobState job) =>
        new(
            AgentCapacityOwnerKind.Job,
            jobKey,
            null,
            job.SubmittedAt!.Value.ToUniversalTime());

    internal static AgentCapacityQueueEntry TurnEntry(AgentSession session, AgentTurnRecord turn)
    {
        var recordedAt = turn.RecordedAt
            ?? throw new InvalidOperationException(
                $"AgentSession {session.Id} queued Turn {turn.Id} has no acceptance timestamp.");
        return new AgentCapacityQueueEntry(
            AgentCapacityOwnerKind.Turn,
            session.Id,
            turn.Id,
            AsUtc(recordedAt),
            turn.Sequence);
    }

    internal static IOrderedEnumerable<AgentCapacityQueueEntry> Order(
        IEnumerable<AgentCapacityQueueEntry> entries) =>
        entries.OrderBy(entry => entry.AcceptedAt)
            .ThenBy(entry => entry.Kind == AgentCapacityOwnerKind.Job ? 0 : 1)
            .ThenBy(entry => entry.OwnerId, StringComparer.Ordinal)
            .ThenBy(entry => entry.TurnSequence)
            .ThenBy(entry => entry.TurnId, StringComparer.Ordinal);

    internal static string? ProjectId(AgentSession session) =>
        session.Metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);

    internal static string? AgentId(AgentSession session) =>
        session.Metadata.Label(GenericAgentSessionMetadata.AgentId);

    /// <summary>
    /// Disposition of one Job's Session-local order evidence. Claimed: the
    /// Job's initial Turn is the referenced Session's current deliverable
    /// head. Incomplete: the persisted owner sources cannot attribute the
    /// reference - the Session row is missing or unreadable, its accepted
    /// identity is blank, a nonblank identity contradicts the Job's own
    /// accepted identity, or the Job's reference tuple is blank - so global
    /// occupancy cannot be claimed complete and no claim may be written.
    /// NotEligible: the reference is definitively wrong or ordered behind an
    /// earlier Turn; evidence stays complete because a definitive mismatch
    /// can never authorize an effect, and it must not be reported as a full
    /// capacity conclusion.
    /// </summary>
    internal static AgentCapacityClaimDisposition EvaluateJobLocalOrder(
        AgentSession? session,
        AgentJobState job,
        string jobKey)
    {
        if (session is null)
            return AgentCapacityClaimDisposition.Incomplete;

        var projectId = ProjectId(session);
        var agentId = AgentId(session);
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(agentId))
            return AgentCapacityClaimDisposition.Incomplete;
        if (!string.Equals(projectId, job.Input?.ProjectId, StringComparison.Ordinal)
            || !string.Equals(agentId, job.Input?.AgentId, StringComparison.Ordinal))
            return AgentCapacityClaimDisposition.Incomplete;

        var turnId = job.Input?.InitialTurnId;
        var inputId = job.Input?.InitialInputId;
        if (string.IsNullOrWhiteSpace(turnId) || string.IsNullOrWhiteSpace(inputId))
            return AgentCapacityClaimDisposition.Incomplete;

        var turn = (session.Status.Turns ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, turnId, StringComparison.Ordinal));
        if (turn is null
            || !string.Equals(turn.JobId, jobKey, StringComparison.Ordinal)
            || !turn.InputIds.Contains(inputId, StringComparer.Ordinal))
            return AgentCapacityClaimDisposition.NotEligible;
        var input = (session.Status.Inputs ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.Id, inputId, StringComparison.Ordinal));
        if (input is null || !string.Equals(input.JobId, jobKey, StringComparison.Ordinal))
            return AgentCapacityClaimDisposition.NotEligible;

        return AgentSessionLocalOrder.IsDeliverableHead(session, turnId, IsManagerRecovery)
            ? AgentCapacityClaimDisposition.Claimed
            : AgentCapacityClaimDisposition.NotEligible;
    }

    // The narrow inspection-only Manager exception to the Unknown hold: only
    // the builtin Manager Agent's recorded recovery Turn may proceed beside
    // an unconfirmed Unknown, and it never resubmits or settles that Unknown.
    internal static bool IsManagerRecovery(AgentSession session, AgentTurnRecord head)
    {
        if (!string.Equals(ProjectId(session), BuiltInAgentCatalog.MohistSlackProjectId, StringComparison.Ordinal)
            || !string.Equals(AgentId(session), $"builtin:{BuiltInAgentCatalog.MohistSlackName}", StringComparison.Ordinal)
            || !head.Id.StartsWith("manager-recovery-turn:", StringComparison.Ordinal))
            return false;
        var inputs = (session.Status.Inputs ?? []).ToDictionary(input => input.Id, StringComparer.Ordinal);
        return head.InputIds.Count > 0
            && head.InputIds.All(id => inputs.TryGetValue(id, out var input)
                && input.Source.StartsWith("manager-recovery:", StringComparison.Ordinal)
                && string.Equals(input.Provenance?.ProviderKind, "slack", StringComparison.Ordinal));
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };
}
