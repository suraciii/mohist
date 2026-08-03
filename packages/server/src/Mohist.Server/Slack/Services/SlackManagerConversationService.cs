using System.Text;
using Mohist.Server.Api;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Sessions.Domain;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;

namespace Mohist.Server.Slack.Services;

public sealed class SlackManagerConversationService : IScopedService, ISlackManagerConversationProcessor
{
    private readonly BuiltInAgentResolver _agents;
    private readonly IAgentLauncher _launcher;
    private readonly AgentSessionQuerier _sessions;
    private readonly AgentSessionFollowupDispatcher _followups;
    private readonly IGrainFactory _grains;
    private readonly SlackManagerToolAuthorization _authorization;
    private readonly SlackManagerApplicationService _manager;
    private readonly AgentConnectionStore _connections;
    private readonly SlackOwnerClaimService _ownerClaims;

    public SlackManagerConversationService(
        BuiltInAgentResolver agents,
        IAgentLauncher launcher,
        AgentSessionQuerier sessions,
        AgentSessionFollowupDispatcher followups,
        IGrainFactory grains,
        SlackManagerToolAuthorization authorization,
        SlackManagerApplicationService manager,
        AgentConnectionStore connections,
        SlackOwnerClaimService ownerClaims)
    {
        _agents = agents;
        _launcher = launcher;
        _sessions = sessions;
        _followups = followups;
        _grains = grains;
        _authorization = authorization;
        _manager = manager;
        _connections = connections;
        _ownerClaims = ownerClaims;
    }

    public async Task<SlackManagerConversationResult> ProcessAsync(
        SlackManagerConversationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = ManagerCommand.Parse(request.Message.Text);
        if (command is not null)
        {
            if (command.IsIncomplete)
                return Reply(request, command.Clarification!);
            return await ExecuteToolAsync(request, command, ct);
        }

        return await ContinueAgentAsync(request, request.Message.Text, ct);
    }

    private async Task<SlackManagerConversationResult> ExecuteToolAsync(
        SlackManagerConversationRequest request,
        ManagerCommand command,
        CancellationToken ct)
    {
        var target = command.Target;
        var decision = await _authorization.AuthorizeAsync(request.Actor, command.Tool, target, ct);
        if (!decision.Allowed)
            return Reply(request, $"I cannot perform that manager action: {decision.Reason}.");

        switch (command.Tool)
        {
            case SlackManagerAgentTools.List:
            case SlackManagerAgentTools.Diagnostics:
                return Reply(request, await RenderStatusAsync(request.Actor.WorkspaceTeamId, ct));
            case SlackManagerAgentTools.View:
                return Reply(request, await RenderAgentsAsync(command.ProjectId!, request.Actor.WorkspaceTeamId, ct));
            case SlackManagerAgentTools.Create:
                return Reply(request, await MountAgentAsync(request, command, ct));
            case SlackManagerAgentTools.Edit:
                return Reply(request, await EditConnectionAsync(command, ct));
            case SlackManagerAgentTools.ClaimOwner:
                return Reply(request, await GenerateOwnerClaimAsync(command, SlackOwnerClaimCodeKinds.Initial, ct));
            case SlackManagerAgentTools.Enable:
            case SlackManagerAgentTools.Disable:
                return Reply(request, await SetConnectionStateAsync(request, command, ct));
            case SlackManagerAgentTools.TransferOwner:
                return Reply(request, await GenerateOwnerClaimAsync(command, SlackOwnerClaimCodeKinds.Transfer, ct));
            default:
                return Reply(request, "That manager action is not available in Slack.");
        }
    }

    private async Task<SlackManagerConversationResult> ContinueAgentAsync(
        SlackManagerConversationRequest request,
        string prompt,
        CancellationToken ct)
    {
        var agent = await _agents.ResolveAsync(BuiltInAgentCatalog.MohistSlackName, ct)
            ?? throw new InvalidOperationException("The built-in mohist-slack Agent is not registered.");
        var sessionId = ManagerSessionId(request);
        var target = await _sessions.ResolveCanonicalFollowupTargetAsync(
            agent.ProjectId,
            sessionId,
            ct);
        if (target is null)
        {
            var launch = await _launcher.LaunchConnectionAsync(
                agent,
                ManagerPrompt(prompt),
                new ConnectionLaunchOrigin(
                    request.Actor.EnrollmentId,
                    request.Actor.WorkspaceTeamId,
                    request.Actor.SlackUserId,
                    request.Message.Identity.ConversationId,
                    request.Message.Identity.MessageTs,
                    request.Message.ThreadTs),
                StartupContext(request.Actor),
                preMintedSessionId: sessionId,
                ct: ct);
            return new SlackManagerConversationResult(
                Text: "Manager request accepted.",
                DispatchRef: $"manager:{launch.SessionId}:{request.Message.Identity.MessageTs}");
        }

        var idempotencyKey = $"manager:{request.Message.Identity.AsKey()}";
        var grain = _grains.GetGrain<IAgentSessionGrain>(sessionId);
        try
        {
            var accepted = await grain.AcceptFollowupAsync(new AcceptFollowupCommand(
                Text: ManagerPrompt(prompt),
                Source: "slack-manager",
                IdempotencyKey: idempotencyKey,
                Provenance: Provenance(request)));
            await _followups.DispatchNextAsync(agent.ProjectId, sessionId, ct);
            return new SlackManagerConversationResult(
                Text: accepted.AlreadyAccepted ? "Manager request is already queued." : "Manager request accepted.",
                DispatchRef: $"manager:{sessionId}:{accepted.InputId}");
        }
        catch (RuntimeSessionMissingException)
        {
            return Reply(request, "The Manager session is not ready yet. Please retry after it connects.");
        }
        catch (SessionActivityUnknownException)
        {
            return Reply(request, "The Manager session state is uncertain. Please retry after it reconciles.");
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
            builder.Append("\n").Append(connection.ProjectId).Append('/').Append(connection.AgentId)
                .Append(": ").Append(connection.DesiredState).Append('/').Append(connection.ConnectionHealth);
        }
        return builder.ToString();
    }

    private async Task<string> RenderAgentsAsync(
        string projectId,
        string workspaceTeamId,
        CancellationToken ct)
    {
        var agents = await _manager.ListAgentOptionsAsync(projectId, workspaceTeamId, ct);
        if (agents.Count == 0)
            return "No active Agents are available in that Project.";
        return string.Join("\n", agents.Select(agent =>
            $"{agent.AgentId} {agent.AgentName}: {(agent.Connection is null ? "not mounted" : agent.Connection.DesiredState)}"));
    }

    private async Task<string> MountAgentAsync(
        SlackManagerConversationRequest request,
        ManagerCommand command,
        CancellationToken ct)
    {
        var result = await _manager.CreateAsync(new SlackManagerCreateRequest(
            command.ProjectId!,
            command.AgentId!,
            request.Actor.WorkspaceTeamId,
            OwnerSlackUserId: request.Actor.SlackUserId), ct);
        return result.Created ? "Agent mounted. The next action is in the manager status." : "Agent is already mounted.";
    }

    private async Task<string> EditConnectionAsync(ManagerCommand command, CancellationToken ct)
    {
        if (command.AccessPolicy is not (
            AccessPolicyKind.OwnerOnly or AccessPolicyKind.Allowlist or AccessPolicyKind.Anyone))
            return "Access policy must be owner_only, allowlist, or anyone.";

        var updated = await _connections.UpdateAsync(
            command.ProjectId!,
            command.ConnectionId!,
            new HashSet<string>(StringComparer.Ordinal) { "accessPolicy" },
            accessPolicy: command.AccessPolicy,
            ct: ct);
        return updated is null ? "The Slack Connection was not found." : "Connection access policy was updated.";
    }

    private async Task<string> SetConnectionStateAsync(
        SlackManagerConversationRequest request,
        ManagerCommand command,
        CancellationToken ct)
    {
        var state = command.Tool == SlackManagerAgentTools.Enable
            ? DesiredStateKind.Enabled
            : DesiredStateKind.Disabled;
        var updated = await _connections.UpdateAsync(
            command.ProjectId!,
            command.ConnectionId!,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: state,
            ct: ct);
        return updated is null ? "The Slack Connection was not found." : $"Connection is now {state}.";
    }

    private async Task<string> GenerateOwnerClaimAsync(
        ManagerCommand command,
        string kind,
        CancellationToken ct)
    {
        try
        {
            var claim = await _ownerClaims.GenerateAsync(
                command.ProjectId!, command.ConnectionId!, kind, ct: ct);
            return $"Send `claim {claim.Value}` to the target Agent App before {claim.ExpiresAt:O}.";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private static SlackManagerConversationResult Reply(
        SlackManagerConversationRequest request,
        string text) =>
        new(text, $"manager:{request.Message.Identity.AsKey()}:reply");

    private static string ManagerSessionId(SlackManagerConversationRequest request) =>
        $"manager-session-{AgentLaunchCoordinatorCodec.StableToken(string.Join('\n',
            request.Actor.EnrollmentId,
            request.Message.Identity.WorkspaceTeamId,
            request.Message.Identity.ConversationId,
            request.Message.ThreadTs ?? request.Message.Identity.MessageTs))}";

    private static AgentStartupContext StartupContext(ManagerActorContext actor) => new(
        $"Authenticated Manager actor for workspace {actor.WorkspaceTeamId}: {actor.SlackUserId}.",
        new AgentStartupContextProvenance("slack-manager", false, null));

    private static AgentSessionInputProvenance Provenance(SlackManagerConversationRequest request) => new(
        ProviderKind: "slack",
        WorkspaceId: request.Message.Identity.WorkspaceTeamId,
        ConversationId: request.Message.Identity.ConversationId,
        ThreadId: request.Message.ThreadTs,
        MemberId: request.Actor.SlackUserId,
        MessageId: request.Message.Identity.MessageTs,
        ConnectionId: request.Actor.EnrollmentId);

    private static string ManagerPrompt(string prompt) =>
        $"Manager request from the authenticated Slack actor. Use only the server-authorized manager tools.\n\n{prompt.Trim()}";

    private sealed record ManagerCommand(
        string Tool,
        string? ProjectId = null,
        string? AgentId = null,
        string? ConnectionId = null,
        string? AccessPolicy = null)
    {
        public bool IsIncomplete =>
            Tool is SlackManagerAgentTools.View && string.IsNullOrWhiteSpace(ProjectId)
            || Tool is SlackManagerAgentTools.Create && (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(AgentId))
            || Tool is SlackManagerAgentTools.Edit or SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable or SlackManagerAgentTools.ClaimOwner
                && (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(ConnectionId))
            || Tool is SlackManagerAgentTools.Edit && string.IsNullOrWhiteSpace(AccessPolicy)
            || Tool is SlackManagerAgentTools.TransferOwner
                && (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(ConnectionId));

        public string? Clarification => Tool switch
        {
            SlackManagerAgentTools.View => "Which Project should I inspect?",
            SlackManagerAgentTools.Create => "Please provide the Project id and Agent id to mount.",
            SlackManagerAgentTools.Edit or SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable or SlackManagerAgentTools.ClaimOwner
                => "Please provide the Project id and Connection id.",
            SlackManagerAgentTools.TransferOwner => "Please provide the Project id and Connection id.",
            _ => null,
        };

        public ManagerResourceTarget? Target => Tool switch
        {
            SlackManagerAgentTools.View => new(ManagerResourceKinds.Project, ProjectId ?? string.Empty),
            SlackManagerAgentTools.Create => new(ManagerResourceKinds.Agent, ProjectId ?? string.Empty, AgentId),
            SlackManagerAgentTools.Edit or SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable
                or SlackManagerAgentTools.ClaimOwner
                or SlackManagerAgentTools.TransferOwner => new(ManagerResourceKinds.Connection, ProjectId ?? string.Empty, ConnectionId),
            _ => null,
        };

        public static ManagerCommand? Parse(string? text)
        {
            var parts = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return null;
            var tool = parts[0];
            if (!SlackManagerAgentTools.IsAllowed(tool) && !SlackManagerAgentTools.IsForbidden(tool))
                return null;
            return parts[0] switch
            {
                SlackManagerAgentTools.View => new(parts[0], ProjectId: parts.ElementAtOrDefault(1)),
                SlackManagerAgentTools.Create => new(parts[0], parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2)),
                SlackManagerAgentTools.ClaimOwner => new(parts[0], parts.ElementAtOrDefault(1), ConnectionId: parts.ElementAtOrDefault(2)),
                SlackManagerAgentTools.Edit => new(parts[0], parts.ElementAtOrDefault(1), ConnectionId: parts.ElementAtOrDefault(2), AccessPolicy: parts.ElementAtOrDefault(3)),
                SlackManagerAgentTools.Enable or SlackManagerAgentTools.Disable => new(parts[0], parts.ElementAtOrDefault(1), ConnectionId: parts.ElementAtOrDefault(2)),
                SlackManagerAgentTools.TransferOwner => new(parts[0], parts.ElementAtOrDefault(1), ConnectionId: parts.ElementAtOrDefault(2)),
                SlackManagerAgentTools.List or SlackManagerAgentTools.Diagnostics => new(parts[0]),
                "remove-binding" or "delete" or "permanent-delete" or "configure" or "rotate-credentials" => new(parts[0]),
                _ => null,
            };
        }
    }
}
