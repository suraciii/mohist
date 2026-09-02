using System.Text.Json;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Slack.Services;

namespace Mohist.Server.Sessions.Grains;

public sealed partial class AgentSessionGrain
{
    public async Task<SlackReplyAnchorValidationResult> ValidateSlackReplyAnchorAsync(SlackReplyAnchorValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.SessionId, SessionId, StringComparison.Ordinal))
            return new SlackReplyAnchorValidationResult(false, false);

        RejectIfReloadRequired();
        var session = _session ?? await _stateStore.LoadAsync(SessionId);
        return session is null
            ? new SlackReplyAnchorValidationResult(false, false)
            : SlackReplyAnchorValidator.Validate(session, request);
    }

    private static string? RedactFailureReason(string? value) =>
        value is null ? null : SlackSecretRedactor.Redact(value);

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
        // input provenance carries the immutable message identity. DM inputs
        // bind to their own triggering message so the terminal delivery lands
        // where the progress projection was posted.
        var threadTs = provenance?.BoundThreadRootMessageId
            ?? provenance?.MessageId
            ?? metadata.Label(AgentSessionQueryMetadataKeys.SlackThreadTs);
        var messageTs = provenance?.MessageId;
        var title = metadata.Label(AgentSessionQueryMetadataKeys.Title);
        var projectId = metadata.Label(AgentSessionQueryMetadataKeys.ProjectId);
        var status = turn.Status switch
        {
            AgentTurnStatus.Cancelled => "failed",
            _ => turn.Status.ToString().ToLowerInvariant(),
        };
        var failureReason = RedactFailureReason(turn.Result?.FailureReason);

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
            failureReason,
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
