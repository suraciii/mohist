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
            workLabel = !string.IsNullOrWhiteSpace(title) ? title : "Follow-up",
            connectionId,
            workspaceTeamId,
            slackUserId = provenance?.MemberId ?? (string?)metadata.Label(AgentSessionQueryMetadataKeys.SlackUserId),
            conversationId,
            threadTs,
            messageTs,
            status,
            message = turn.Result?.Message,
            failureReason = (string?)null,
            failureCategory = (string?)null,
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
