using System.Text;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerToolExecutor : IScopedService
{
    private readonly SlackManagerToolAuthorization _authorization;
    private readonly SlackManagerApplicationService _manager;
    private readonly AgentQuerier _agents;
    private readonly AgentConnectionStore _connections;
    private readonly SlackOwnerClaimService _ownerClaims;
    private readonly ManagerAgentDefaultProfileResolver _defaults;
    private readonly IGrainFactory _grains;

    public SlackManagerToolExecutor(
        SlackManagerToolAuthorization authorization,
        SlackManagerApplicationService manager,
        AgentQuerier agents,
        AgentConnectionStore connections,
        SlackOwnerClaimService ownerClaims,
        ManagerAgentDefaultProfileResolver defaults,
        IGrainFactory grains)
    {
        _authorization = authorization;
        _manager = manager;
        _agents = agents;
        _connections = connections;
        _ownerClaims = ownerClaims;
        _defaults = defaults;
        _grains = grains;
    }

    public async Task<SlackManagerToolExecution> ExecuteAsync(
        ManagerActorContext actor,
        SlackManagerToolInvocation invocation,
        string invocationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(invocation);

        var decision = await _authorization.AuthorizeAsync(actor, invocation.Tool, invocation.Target, ct);
        if (!decision.Allowed)
            return Fail(invocation.Tool, decision.Reason ?? "manager_tool_not_authorized");

        try
        {
            return invocation.Tool switch
            {
                SlackManagerAgentTools.List or SlackManagerAgentTools.Diagnostics =>
                    Succeed(invocation.Tool, await RenderStatusAsync(actor.WorkspaceTeamId, ct)),
                SlackManagerAgentTools.View =>
                    Succeed(invocation.Tool, await RenderAgentsAsync(invocation.ProjectId!, actor.WorkspaceTeamId, ct)),
                SlackManagerAgentTools.Create =>
                    await MountAgentAsync(actor, invocation, invocationId, ct),
                SlackManagerAgentTools.Edit =>
                    await EditConnectionAsync(invocation, ct),
                SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable =>
                    await SetConnectionStateAsync(invocation, ct),
                SlackManagerAgentTools.ClaimOwner =>
                    await GenerateOwnerClaimAsync(invocation, SlackOwnerClaimCodeKinds.Initial, ct),
                SlackManagerAgentTools.TransferOwner =>
                    await GenerateOwnerClaimAsync(invocation, SlackOwnerClaimCodeKinds.Transfer, ct),
                _ => Fail(invocation.Tool, "manager_tool_not_authorized"),
            };
        }
        catch (SlackManagerValidationException ex)
        {
            return Fail(invocation.Tool, ex.Message, ex.Code);
        }
        catch (SlackManagerConflictException ex)
        {
            return Fail(invocation.Tool, ex.Message, ex.Code);
        }
        catch (AgentNameConflictException ex)
        {
            return Fail(invocation.Tool, ex.Message, "agent_name_conflict");
        }
        catch (InvalidOperationException ex)
        {
            return Fail(invocation.Tool, ex.Message, "manager_tool_unavailable");
        }
    }

    private async Task<string> RenderStatusAsync(string workspaceTeamId, CancellationToken ct)
    {
        var status = await _manager.GetStatusAsync(workspaceTeamId, ct);
        if (status is null)
            return "Manager setup is not available for this workspace.";

        var builder = new StringBuilder();
        builder.Append("Manager status: ").Append(status.NextAction).Append('.');
        foreach (var connection in status.Connections)
        {
            builder.Append('\n').Append(connection.ProjectId).Append('/').Append(connection.AgentId)
                .Append(": ").Append(connection.DesiredState).Append('/').Append(connection.ConnectionHealth);
        }
        return builder.ToString();
    }

    private async Task<string> RenderAgentsAsync(string projectId, string workspaceTeamId, CancellationToken ct)
    {
        var agents = await _manager.ListAgentOptionsAsync(projectId, workspaceTeamId, ct);
        return agents.Count == 0
            ? "No active Agents are available in that Project."
            : string.Join('\n', agents.Select(agent =>
                $"{agent.AgentId} {agent.AgentName}: {(agent.Connection is null ? "not mounted" : agent.Connection.DesiredState)}"));
    }

    private async Task<SlackManagerToolExecution> MountAgentAsync(
        ManagerActorContext actor,
        SlackManagerToolInvocation invocation,
        string invocationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invocation.ProjectId) || string.IsNullOrWhiteSpace(invocation.AgentName))
            return Fail(invocation.Tool, "Please provide the Project id and Agent name to mount or create.", "manager_tool_arguments_missing");

        var agent = await _agents.GetByIdAsync(invocation.ProjectId, invocation.AgentName, ct)
            ?? await _agents.GetByNameAsync(invocation.ProjectId, invocation.AgentName);
        var created = agent is null;
        if (agent is null)
        {
            if (string.IsNullOrWhiteSpace(invocation.DailyResponsibility))
                return Fail(invocation.Tool, "Please provide the Agent's daily responsibility before creating it.", "manager_tool_arguments_missing");

            var responsibility = invocation.DailyResponsibility.Trim();
            var agentId = $"agent_{AgentLaunchCoordinatorCodec.StableToken(invocationId)}";
            var profile = _defaults.Resolve();
            var grain = _grains.GetGrain<IAgentGrain>(GrainKey.Agent(invocation.ProjectId, agentId));
            agent = await grain.CreateAsync(new AgentCreateData(
                invocation.ProjectId,
                invocation.AgentName,
                $"Helps with {responsibility}.",
                $"You are responsible for {responsibility}.",
                profile.ToAgentConfig(),
                Skills: [],
                MaxConcurrentRuns: null));
        }

        var result = await _manager.CreateAsync(new SlackManagerCreateRequest(
            invocation.ProjectId,
            agent.Id,
            actor.WorkspaceTeamId,
            OwnerSlackUserId: actor.SlackUserId), ct);
        return created
            ? Succeed(invocation.Tool, "Agent created and mounted. The next action is in the manager status.")
            : result.Created
                ? Succeed(invocation.Tool, "Agent mounted. The next action is in the manager status.")
                : Succeed(invocation.Tool, "Agent is already mounted.");
    }

    private async Task<SlackManagerToolExecution> EditConnectionAsync(
        SlackManagerToolInvocation invocation,
        CancellationToken ct)
    {
        if (invocation.AccessPolicy is not (
            AccessPolicyKind.OwnerOnly or AccessPolicyKind.Allowlist or AccessPolicyKind.Anyone))
        {
            return Fail(invocation.Tool,
                "Access policy must be owner_only, allowlist, or anyone.",
                "manager_tool_arguments_invalid");
        }

        var updated = await _connections.UpdateAsync(
            invocation.ProjectId!,
            invocation.ConnectionId!,
            new HashSet<string>(StringComparer.Ordinal) { "accessPolicy" },
            accessPolicy: invocation.AccessPolicy,
            ct: ct);
        return updated is null
            ? Fail(invocation.Tool, "The Slack Connection was not found.", "manager_resource_not_found")
            : Succeed(invocation.Tool, "Connection access policy was updated.");
    }

    private async Task<SlackManagerToolExecution> SetConnectionStateAsync(
        SlackManagerToolInvocation invocation,
        CancellationToken ct)
    {
        var state = invocation.Tool == SlackManagerAgentTools.Enable
            ? DesiredStateKind.Enabled
            : DesiredStateKind.Disabled;
        var updated = await _connections.UpdateAsync(
            invocation.ProjectId!,
            invocation.ConnectionId!,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: state,
            ct: ct);
        return updated is null
            ? Fail(invocation.Tool, "The Slack Connection was not found.", "manager_resource_not_found")
            : Succeed(invocation.Tool, $"Connection is now {state}.");
    }

    private async Task<SlackManagerToolExecution> GenerateOwnerClaimAsync(
        SlackManagerToolInvocation invocation,
        string kind,
        CancellationToken ct)
    {
        try
        {
            var claim = await _ownerClaims.GenerateAsync(
                invocation.ProjectId!, invocation.ConnectionId!, kind, ct: ct);
            return Succeed(invocation.Tool,
                $"Send `claim {claim.Value}` to the target Agent App before {claim.ExpiresAt:O}.");
        }
        catch (InvalidOperationException ex)
        {
            return Fail(invocation.Tool, ex.Message, "manager_tool_unavailable");
        }
    }

    private static SlackManagerToolExecution Succeed(string tool, string message) =>
        new(tool, true, message, null);

    private static SlackManagerToolExecution Fail(string tool, string message, string? code = null) =>
        new(tool, false, message, code);
}

public sealed record SlackManagerToolExecution(
    string Tool,
    bool Succeeded,
    string Message,
    string? Code);
