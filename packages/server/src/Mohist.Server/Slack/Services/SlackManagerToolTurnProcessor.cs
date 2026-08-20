using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Slack.Services;

// Retired with the model-output protocol. Kept as source history for the
// migration window, but deliberately not registered as an application service.
public sealed class SlackManagerToolTurnProcessor
{
    private readonly ManagerActorAccessDecider _access;
    private readonly SlackManagerToolExecutor _tools;
    private readonly SlackManagerToolExecutionFenceStore _fences;
    private readonly SlackOutboxStore _outbox;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly IGrainFactory _grains;

    public SlackManagerToolTurnProcessor(
        ManagerActorAccessDecider access,
        SlackManagerToolExecutor tools,
        SlackManagerToolExecutionFenceStore fences,
        SlackOutboxStore outbox,
        AgentSessionFollowupDispatcher followups,
        IGrainFactory grains)
    {
        _access = access;
        _tools = tools;
        _fences = fences;
        _outbox = outbox;
        _followups = followups;
        _grains = grains;
    }

    public async Task<bool> ProcessAsync(
        string sessionId,
        SlackTerminalDelivery delivery,
        CancellationToken ct = default)
    {
        if (!string.Equals(delivery.Status, "completed", StringComparison.Ordinal))
            return false;

        var intent = SlackManagerToolInvocation.Parse(delivery.AssistantText);
        if (!intent.IsRequested)
            return false;

        if (!await _fences.TryAcquireAsync(delivery.JobKey, sessionId, ct))
            return true;

        var execution = intent.Invocation is null
            ? new SlackManagerToolExecution("unknown", false,
                intent.Error ?? "manager_tool_request_invalid", intent.Error)
            : await ExecuteAuthorizedAsync(delivery, intent.Invocation, ct);

        if (!string.IsNullOrWhiteSpace(execution.UserVisibleMessage))
        {
            var dispatchRef = $"manager-tool:{delivery.JobKey}:user-instruction";
            await _outbox.EnqueueAsync(new SlackOutboxDraft(
                SlackDeliveryOwnerIds.ManagerProjectId,
                delivery.ConnectionId,
                delivery.WorkspaceTeamId,
                delivery.ConversationId,
                SlackOutboxKinds.UserAction,
                dispatchRef,
                JsonSerializer.Serialize(new SlackDeliveryPayload(
                    SlackDeliveryOperations.PostMessage,
                    execution.UserVisibleMessage,
                    ClientMessageId: dispatchRef)),
                delivery.ThreadTs ?? delivery.MessageTs,
                SlackDeliveryOwnerKinds.Manager), ct);
        }

        var resultText = JsonSerializer.Serialize(new
        {
            managerToolResult = new
            {
                tool = execution.Tool,
                succeeded = execution.Succeeded,
                code = execution.Code,
                message = execution.Message,
            },
        });
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
            Text: "The server completed the requested manager tool call. Treat this result as authoritative. "
                + "Reply to the user in natural language and do not expose this protocol.\n\n"
                + resultText,
            Source: "slack-manager-tool",
            IdempotencyKey: $"manager-tool:{delivery.JobKey}"));
        await _followups.DispatchNextAsync(BuiltInAgentCatalog.MohistSlackProjectId, sessionId, ct);
        await _fences.MarkCompletedAsync(delivery.JobKey, ct);
        return true;
    }

    private async Task<SlackManagerToolExecution> ExecuteAuthorizedAsync(
        SlackTerminalDelivery delivery,
        SlackManagerToolInvocation invocation,
        CancellationToken ct)
    {
        var actor = await _access.AuthenticateAsync(
            delivery.WorkspaceTeamId,
            delivery.SlackUserId ?? string.Empty,
            ct);
        if (!actor.Allowed || actor.Actor is null)
        {
            return new SlackManagerToolExecution(
                invocation.Tool,
                false,
                "manager_actor_not_authorized",
                "manager_actor_not_authorized");
        }

        return await _tools.ExecuteAsync(actor.Actor, invocation, delivery.JobKey, ct);
    }
}
