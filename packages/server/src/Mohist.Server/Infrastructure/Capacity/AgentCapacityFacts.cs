using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Infrastructure.Capacity;

internal static class AgentCapacityFacts
{
    internal static bool Occupies(AgentJobState job) =>
        job.CapacityClaimedAt is not null
        && job.TerminalAt is null
        && job.Status is AgentJobStatus.Pending or AgentJobStatus.Running or AgentJobStatus.Unknown;

    internal static bool Occupies(AgentSession session, AgentTurnRecord turn) =>
        turn.CapacityClaimedAt is not null
        && string.IsNullOrWhiteSpace(turn.JobId)
        && turn.SupersededAt is null
        && turn.ContextGeneration == session.Status.ContextGeneration
        && turn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing or AgentTurnStatus.Unknown;

    internal static bool IsEligible(AgentJobState job) =>
        job.Status == AgentJobStatus.Pending
        && job.TerminalAt is null
        && job.CapacityClaimedAt is null
        && job.SubmittedAt is not null
        && !string.IsNullOrWhiteSpace(job.Input?.ProjectId)
        && !string.IsNullOrWhiteSpace(job.Input?.AgentId);

    internal static AgentTurnRecord? EligibleHead(AgentSession session)
    {
        var turns = session.Status.Turns ?? [];
        var current = turns
            .Where(turn => turn.SupersededAt is null
                && turn.ContextGeneration == session.Status.ContextGeneration)
            .ToArray();

        if (session.Status.PendingStop is { IsActive: true }
            || session.Status.PendingReset is { Outcome: null, SupersededAt: null }
            || current.Any(turn => !string.IsNullOrWhiteSpace(turn.JobId)
                && turn.Status == AgentTurnStatus.Queued)
            || current.Any(turn => turn.Status == AgentTurnStatus.Executing)
            || HasConfirmedOwner(session, current))
            return null;

        var head = current
            .Where(turn => string.IsNullOrWhiteSpace(turn.JobId)
                && turn.Status == AgentTurnStatus.Queued)
            .OrderBy(turn => turn.Sequence)
            .ThenBy(turn => turn.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (head is null || head.CapacityClaimedAt is not null)
            return null;

        // Only the system Manager inspection Turn may proceed beside an
        // unconfirmed Unknown. An ordinary queued follow-up must not turn a
        // lost response into permission for another effect.
        if (current.Any(turn => turn.Status == AgentTurnStatus.Unknown)
            && !IsManagerRecovery(session, head))
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

    private static bool HasConfirmedOwner(
        AgentSession session,
        IReadOnlyCollection<AgentTurnRecord> current) =>
        session.Status.ConfirmedExecutionOwnership is { } ownership
        && ownership.ContextGeneration == session.Status.ContextGeneration
        && current.Any(turn => ownership.TurnIds.Contains(turn.Id, StringComparer.Ordinal)
            && turn.Status is AgentTurnStatus.Executing or AgentTurnStatus.Unknown);

    private static bool IsManagerRecovery(AgentSession session, AgentTurnRecord head)
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
