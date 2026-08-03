using System.Text.Json;
using Mohist.Server.Api;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Slack;
using Mohist.Server.Sessions.Grains;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerToolTurnProcessor : IScopedService
{
    private readonly ManagerActorAccessDecider _access;
    private readonly SlackManagerToolExecutor _tools;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly IGrainFactory _grains;

    public SlackManagerToolTurnProcessor(
        ManagerActorAccessDecider access,
        SlackManagerToolExecutor tools,
        AgentSessionFollowupDispatcher followups,
        IGrainFactory grains)
    {
        _access = access;
        _tools = tools;
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

        var execution = intent.Invocation is null
            ? new SlackManagerToolExecution("unknown", false,
                intent.Error ?? "manager_tool_request_invalid", intent.Error)
            : await ExecuteAuthorizedAsync(delivery, intent.Invocation, ct);

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
