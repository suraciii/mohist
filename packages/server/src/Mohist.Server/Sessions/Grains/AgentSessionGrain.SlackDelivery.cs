using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<SlackReplyAnchorValidationResult> ValidateSlackReplyAnchorAsync(SlackReplyAnchorValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SessionId, SessionId, StringComparison.Ordinal))
            return InvalidSlackReplyAnchor();

        RejectIfReloadRequired();
        var session = _session ?? await _stateStore.LoadAsync(SessionId);
        if (session is null)
            return InvalidSlackReplyAnchor();
        var metadata = session.Metadata;
        if (!string.Equals(metadata?.Label(AgentSessionQueryMetadataKeys.ProjectId), request.ProjectId, StringComparison.Ordinal))
            return InvalidSlackReplyAnchor();

        var inputs = session.Status.Inputs ?? [];
        var turns = session.Status.Turns ?? [];
        var initialInput = inputs
            .Where(input => !string.IsNullOrWhiteSpace(input.JobId))
            .OrderBy(input => input.Sequence)
            .FirstOrDefault();
        var initialProvenance = initialInput?.Provenance;
        var durableRoot = !string.IsNullOrWhiteSpace(initialProvenance?.BoundThreadRootMessageId)
            ? initialProvenance.BoundThreadRootMessageId
            : !string.IsNullOrWhiteSpace(initialProvenance?.ThreadId)
                ? initialProvenance.ThreadId
                : initialProvenance?.MessageId;
        if (string.IsNullOrWhiteSpace(durableRoot)
            || !string.Equals(durableRoot, request.ThreadRootMessageId, StringComparison.Ordinal))
            return InvalidSlackReplyAnchor();

        var matchingInputs = inputs.Where(input => MatchesSlackReplyProvenance(input.Provenance, request)).ToArray();
        if (matchingInputs.Length != 1)
            return InvalidSlackReplyAnchor();
        var input = matchingInputs[0];

        if (string.Equals(input.Id, initialInput?.Id, StringComparison.Ordinal))
        {
            if (!string.Equals(request.DispatchRef, $"slack:{SessionId}:{input.Id}", StringComparison.Ordinal))
                return InvalidSlackReplyAnchor();
            var initialTurn = turns.FirstOrDefault(turn =>
                !string.IsNullOrWhiteSpace(turn.JobId)
                && turn.InputIds.Contains(input.Id, StringComparer.Ordinal));
            if (initialTurn is null)
                return InvalidSlackReplyAnchor();
            return new SlackReplyAnchorValidationResult(
                Valid: true,
                TurnActive: initialTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
        }

        var followupTurn = turns.SingleOrDefault(turn =>
            string.IsNullOrWhiteSpace(turn.JobId)
            && turn.InputIds.Contains(input.Id, StringComparer.Ordinal));
        if (followupTurn is null)
            return InvalidSlackReplyAnchor();
        var expectedDispatchRef = followupTurn.OperationId
            ?? GetPendingFollowups(session)
                .SingleOrDefault(lease => string.Equals(lease.TurnId, followupTurn.Id, StringComparison.Ordinal))
                ?.OperationId;
        if (string.IsNullOrWhiteSpace(expectedDispatchRef))
        {
            return followupTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing
                ? InvalidSlackReplyAnchor()
                : new SlackReplyAnchorValidationResult(Valid: true, TurnActive: false);
        }
        if (!string.Equals(request.DispatchRef, expectedDispatchRef, StringComparison.Ordinal))
            return InvalidSlackReplyAnchor();
        return new SlackReplyAnchorValidationResult(
            Valid: true,
            TurnActive: followupTurn.Status is AgentTurnStatus.Queued or AgentTurnStatus.Executing);
    }

    private static SlackReplyAnchorValidationResult InvalidSlackReplyAnchor() => new(false, false);

    private static bool MatchesSlackReplyProvenance(
        AgentSessionInputProvenance? provenance,
        SlackReplyAnchorValidationRequest request) =>
        provenance is not null
        && string.Equals(provenance.ProviderKind, "slack", StringComparison.Ordinal)
        && string.Equals(provenance.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(provenance.ConnectionId, request.ConnectionId, StringComparison.Ordinal)
        && string.Equals(provenance.ConversationId, request.ConversationId, StringComparison.Ordinal)
        && string.Equals(provenance.MessageId, request.TriggeringMessageId, StringComparison.Ordinal);

    private async Task TryEmitFollowupDeliveryAsync(AgentSession session, AgentTurnRecord turn)
    {
        var metadata = session.Metadata;
        if (metadata is null) return;

        var input = (session.Status.Inputs ?? [])
            .FirstOrDefault(candidate => turn.InputIds.Contains(candidate.Id, StringComparer.Ordinal));
        var provenance = input?.Provenance;
        var connectionId = provenance?.ConnectionId ?? metadata.Label(AgentSessionQueryMetadataKeys.ConnectionId);
        var workspaceTeamId = provenance?.WorkspaceId ?? metadata.Label(AgentSessionQueryMetadataKeys.SlackWorkspaceTeamId);
        var conversationId = provenance?.ConversationId ?? metadata.Label(AgentSessionQueryMetadataKeys.SlackConversationId);
        if (string.IsNullOrWhiteSpace(connectionId)
            || string.IsNullOrWhiteSpace(workspaceTeamId)
            || string.IsNullOrWhiteSpace(conversationId))
            return;

        // Follow-up terminal delivery must target the message that created
        // this turn. Session metadata identifies the conversation, while the
        // input provenance carries the immutable message identity.
        var threadTs = provenance?.BoundThreadRootMessageId
            ?? metadata.Label(AgentSessionQueryMetadataKeys.SlackThreadTs);
        var messageTs = provenance?.MessageId;
        var title = metadata.Label(AgentSessionQueryMetadataKeys.Title);
        var projectId = metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var status = turn.Status switch
        {
            AgentTurnStatus.Cancelled => "failed",
            _ => turn.Status.ToString().ToLowerInvariant(),
        };

        var delivery = new
        {
            jobKey = $"agent-session-followup:{session.Id}:{turn.Id}",
            sessionId = session.Id,
            turnId = turn.Id,
            workLabel = !string.IsNullOrWhiteSpace(title) ? title : "Follow-up",
            connectionId,
            workspaceTeamId,
            slackUserId = provenance?.MemberId ?? (string?)metadata.Label(AgentSessionQueryMetadataKeys.SlackUserId),
            conversationId,
            threadTs,
            messageTs,
            status,
            message = turn.Result?.Message,
            failureReason = turn.Result?.FailureReason,
            failureCategory = turn.Result?.FailureCategory,
            artifactCount = 0,
            exitCode = (int?)null,
            assistantText = string.Equals(projectId, SlackDeliveryOwnerIds.ManagerProjectId, StringComparison.Ordinal)
                ? null
                : AgentJobLineage.ExtractAssistantText(turn.Result?.Output),
        };
        var data = JsonSerializer.SerializeToElement(delivery, CloudEvent.JsonOptions);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(projectId))
            extensions[EventCatalog.Lineage.ProjectId] = projectId;

        var envelope = new CloudEvent(
            id: $"followup-delivery:{session.Id}:{turn.Id}",
            source: new Uri($"/mohist/agent-session/{session.Id}", UriKind.Relative),
            type: EventCatalog.ReverseDns.AgentSessionFollowupDelivery,
            time: _timeProvider.GetUtcNow(),
            data: data,
            subject: session.Id,
            extensions: extensions);

        try
        {
            await _eventStore.AppendAsync(envelope, CancellationToken.None);
            _dispatchSignal.Wake();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "AgentSession {SessionId} follow-up delivery event could not be emitted for turn {TurnId}",
                session.Id,
                turn.Id);
        }
    }
}
