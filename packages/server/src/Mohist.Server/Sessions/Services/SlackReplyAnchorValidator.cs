using Mohist.Server.Contracts;
using Mohist.Server.Sessions.Domain;

namespace Mohist.Server.Sessions.Services;

/// <summary>
/// Validates the immutable Slack reply identity against a Session snapshot.
/// Keeping this decision independent from the grain lets ingress and component
/// tests exercise the same provenance contract without starting a host.
/// </summary>
internal static class SlackReplyAnchorValidator
{
    public static SlackReplyAnchorValidationResult Validate(
        AgentSession session,
        SlackReplyAnchorValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.SessionId, session.Id, StringComparison.Ordinal))
            return Invalid();

        var metadata = session.Metadata;
        if (!string.Equals(metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId), request.ProjectId, StringComparison.Ordinal))
            return Invalid();

        var inputs = session.Status.Inputs ?? [];
        var turns = session.Status.Turns ?? [];
        var initialInput = inputs
            .Where(input => !string.IsNullOrWhiteSpace(input.JobId))
            .OrderBy(input => input.Sequence)
            .FirstOrDefault();
        var initialProvenance = initialInput?.Provenance;

        var matchingInputs = inputs.Where(input => MatchesProvenance(input.Provenance, request)).ToArray();
        if (matchingInputs.Length != 1)
            return Invalid();
        var input = matchingInputs[0];

        // A threaded follow-up must use the Session's durable bound root. An
        // unthreaded input is rooted at its own triggering message.
        var expectedThreadRoot = !string.IsNullOrWhiteSpace(input.Provenance!.ThreadId)
            ? DurableBoundRoot(initialProvenance)
            : input.Provenance.MessageId;
        if (string.IsNullOrWhiteSpace(expectedThreadRoot)
            || !string.Equals(expectedThreadRoot, request.ThreadRootMessageId, StringComparison.Ordinal))
            return Invalid();

        if (string.Equals(input.Id, initialInput?.Id, StringComparison.Ordinal))
        {
            if (!string.Equals(request.DispatchRef, $"slack:{session.Id}:{input.Id}", StringComparison.Ordinal))
                return Invalid();
            var initialTurn = turns.FirstOrDefault(turn =>
                !string.IsNullOrWhiteSpace(turn.JobId)
                && turn.InputIds.Contains(input.Id, StringComparer.Ordinal));
            if (initialTurn is null)
                return Invalid();
            return new SlackReplyAnchorValidationResult(
                Valid: true,
                TurnActive: initialTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
        }

        var followupTurn = turns.SingleOrDefault(turn =>
            string.IsNullOrWhiteSpace(turn.JobId)
            && turn.InputIds.Contains(input.Id, StringComparer.Ordinal));
        if (followupTurn is null)
            return Invalid();
        var expectedDispatchRef = followupTurn.OperationId
            ?? GetPendingFollowups(session)
                .SingleOrDefault(lease => string.Equals(lease.TurnId, followupTurn.Id, StringComparison.Ordinal))
                ?.OperationId;
        if (string.IsNullOrWhiteSpace(expectedDispatchRef))
        {
            return followupTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing
                ? Invalid()
                : new SlackReplyAnchorValidationResult(Valid: true, TurnActive: false);
        }
        if (!string.Equals(request.DispatchRef, expectedDispatchRef, StringComparison.Ordinal))
            return Invalid();
        return new SlackReplyAnchorValidationResult(
            Valid: true,
            TurnActive: followupTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
    }

    private static SlackReplyAnchorValidationResult Invalid() => new(false, false);

    private static string? DurableBoundRoot(AgentSessionInputProvenance? provenance) =>
        provenance?.BoundThreadRootMessageId;

    private static bool MatchesProvenance(
        AgentSessionInputProvenance? provenance,
        SlackReplyAnchorValidationRequest request) =>
        provenance is not null
        && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
        && string.Equals(provenance.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(provenance.ConnectionId, request.ConnectionId, StringComparison.Ordinal)
        && string.Equals(provenance.ConversationId, request.ConversationId, StringComparison.Ordinal)
        && string.Equals(provenance.MessageId, request.TriggeringMessageId, StringComparison.Ordinal);

    private static IReadOnlyList<AgentSessionFollowupLease> GetPendingFollowups(AgentSession session)
    {
        if (session.Status.PendingFollowups is { Count: > 0 } pending)
            return pending;
        return session.Status.PendingFollowup is null ? [] : [session.Status.PendingFollowup];
    }
}
