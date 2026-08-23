namespace Mohist.Server.Slack;

internal enum SlackMultiAgentRoutingDisposition
{
    Ignore,
    RejectNonOwner,
    Prompt,
}

internal sealed record SlackMultiAgentRoutingCandidate(
    string ProjectId,
    string ConnectionId,
    string BotUserId,
    string? OwnerSlackUserId,
    string? SessionId = null,
    string? RootMessageTs = null)
{
    public SlackMultiAgentRoutingCandidate(
        string connectionId,
        string botUserId,
        string? ownerSlackUserId)
        : this("", connectionId, botUserId, ownerSlackUserId)
    {
    }
}

internal sealed record SlackMultiAgentRoutingDecision(
    SlackMultiAgentRoutingDisposition Disposition,
    IReadOnlyList<SlackMultiAgentRoutingCandidate> Candidates,
    IReadOnlyList<string> ConnectionIds,
    IReadOnlyList<string> BotLabels);

/// <summary>
/// Decides which side effect a multi-Bot channel ingress may perform. The
/// route owns database lookup and delivery; this component owns only the
/// deterministic attribution rule so its decision matrix can run at L0.
/// </summary>
internal static class SlackMultiAgentRoutingPolicy
{
    public static SlackMultiAgentRoutingDecision? Decide(
        string currentConnectionId,
        string senderSlackUserId,
        bool senderAuthorizedForCurrentConnection,
        IReadOnlyList<SlackMultiAgentRoutingCandidate> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentConnectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderSlackUserId);
        ArgumentNullException.ThrowIfNull(candidates);

        var distinctCandidates = candidates
            .GroupBy(candidate => candidate.BotUserId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (distinctCandidates.Length < 2)
            return null;
        var connectionIds = distinctCandidates
            .Select(candidate => candidate.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ownerClaimantConnectionId = distinctCandidates
            .Where(candidate => string.Equals(
                candidate.OwnerSlackUserId, senderSlackUserId, StringComparison.Ordinal))
            .Select(candidate => candidate.ConnectionId)
            .FirstOrDefault();
        var currentConnectionIsMentioned = connectionIds.Contains(
            currentConnectionId, StringComparer.Ordinal);

        var disposition = !currentConnectionIsMentioned
            || (ownerClaimantConnectionId is not null
                && !senderAuthorizedForCurrentConnection
                && !string.Equals(ownerClaimantConnectionId, currentConnectionId, StringComparison.Ordinal))
            ? SlackMultiAgentRoutingDisposition.Ignore
            : !senderAuthorizedForCurrentConnection
                ? SlackMultiAgentRoutingDisposition.RejectNonOwner
                : SlackMultiAgentRoutingDisposition.Prompt;

        return new SlackMultiAgentRoutingDecision(
            disposition,
            distinctCandidates,
            connectionIds,
            distinctCandidates.Select(candidate => candidate.BotUserId).ToArray());
    }
}
