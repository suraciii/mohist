namespace Mohist.Server.Slack;

internal enum SlackMultiAgentRoutingDisposition
{
    Ignore,
    RejectNonOwner,
    Prompt,
}

internal sealed record SlackMultiAgentRoutingCandidate(
    string ConnectionId,
    string BotUserId,
    string? OwnerSlackUserId);

internal sealed record SlackMultiAgentRoutingDecision(
    SlackMultiAgentRoutingDisposition Disposition,
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

        if (candidates.Count < 2)
            return null;

        var connectionIds = candidates
            .Select(candidate => candidate.ConnectionId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ownerClaimantConnectionId = candidates
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
            connectionIds,
            candidates.Select(candidate => candidate.BotUserId).ToArray());
    }
}
