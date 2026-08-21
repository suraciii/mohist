using System.Text.Json;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Grains;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Agent.Services;
using Mohist.Server.Infrastructure.Data.Slack;
using Mohist.Server.Infrastructure.Slack;

namespace Mohist.Server.Slack.Services;

public sealed partial class SlackManagerApplicationService
{
    public async Task<AgentInfo?> GetAgentAsync(
        string projectId,
        string agentId,
        CancellationToken ct = default) =>
        await _agents.GetByIdAsync(projectId, agentId, ct);

    public async Task<AgentInfo?> GetAgentByNameAsync(
        string projectId,
        string agentName,
        CancellationToken ct = default) =>
        await _agents.GetByNameAsync(projectId, agentName);

    public async Task<SlackManagerConnectionInspection?> InspectConnectionAsync(
        string projectId,
        string connectionId,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        return new(ProjectConnection(connection), await GetAsync(projectId, connectionId, ct));
    }

    public async Task<SlackManagerCreateResult> CreateOrMountAsync(
        string projectId,
        string? agentId,
        string? agentName,
        string? responsibility,
        string workspaceTeamId,
        string ownerSlackUserId,
        string? accessPolicy = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(agentId) == !string.IsNullOrWhiteSpace(agentName))
            throw new SlackManagerValidationException(
                "Exactly one of agentId or agentName is required.",
                "agent_reference_required");

        AgentInfo? agent = null;
        var created = false;
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            agent = await _agents.GetByIdAsync(projectId, agentId.Trim(), ct);
        }
        else
        {
            agent = await _agents.GetByNameAsync(projectId, agentName!.Trim());
            if (agent is null)
            {
                if (string.IsNullOrWhiteSpace(responsibility))
                    throw new SlackManagerValidationException(
                        "responsibility is required when agentName does not resolve to an existing Agent.",
                        "responsibility_required");

                var trimmedName = agentName.Trim();
                var trimmedResponsibility = responsibility.Trim();
                var newAgentId = $"agent_{AgentLaunchCoordinatorCodec.StableToken(
                    $"manager-create\n{projectId}\n{workspaceTeamId}\n{trimmedName}")}";
                var grain = _grains.GetGrain<IAgentGrain>(GrainKey.Agent(projectId, newAgentId));
                try
                {
                    agent = await grain.CreateAsync(new AgentCreateData(
                        projectId,
                        trimmedName,
                        $"Helps with {trimmedResponsibility}.",
                        $"You are responsible for {trimmedResponsibility}.",
                        _defaults.Resolve().ToAgentConfig(),
                        Skills: [],
                        MaxConcurrentRuns: null,
                        Purpose: trimmedResponsibility));
                    created = true;
                }
                catch (AgentNameConflictException)
                {
                    agent = await _agents.GetByNameAsync(projectId, trimmedName);
                    if (agent is null)
                        throw new SlackManagerConflictException(
                            "An Agent with that name already exists but could not be loaded.",
                            "agent_name_conflict");
                }
            }
        }

        if (agent is null)
            throw new SlackManagerValidationException("The Agent was not found.", "agent_not_found");

        var mounted = await CreateAsync(new SlackManagerCreateRequest(
            projectId,
            agent.Id,
            workspaceTeamId,
            accessPolicy ?? AccessPolicyKind.OwnerOnly,
            ownerSlackUserId), ct);
        return created ? mounted with { Created = true } : mounted;
    }

    public async Task<AgentConnection?> SetDesiredStateAsync(
        string projectId,
        string connectionId,
        string desiredState,
        CancellationToken ct = default)
    {
        if (desiredState is not (DesiredStateKind.Enabled or DesiredStateKind.Disabled))
            throw new SlackManagerValidationException("Unknown Connection state.", "invalid_desired_state");

        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        if (connection.DesiredState == desiredState) return connection;

        var updated = await _connections.UpdateAsync(
            projectId,
            connectionId,
            new HashSet<string>(StringComparer.Ordinal) { "desiredState" },
            desiredState: desiredState,
            ct: ct);
        if (updated is not null && desiredState == DesiredStateKind.Enabled)
            await _outbox.PrunePendingStatusMutationsAsync(projectId, connectionId, ct);
        return updated;
    }

    public async Task<SlackManagerAccessPolicyResult?> EditAccessPolicyAsync(
        string projectId,
        string connectionId,
        string accessPolicy,
        IReadOnlyList<string>? allowMembers = null,
        CancellationToken ct = default)
    {
        var replaced = await _accessPolicies.ReplaceAsync(
            projectId,
            connectionId,
            accessPolicy,
            allowMembers ?? Array.Empty<string>(),
            ct);
        if (!replaced) return null;

        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        return new(
            ProjectConnection(connection),
            connection.AccessPolicy,
            await _accessPolicies.ListMembersAsync(projectId, connectionId, ct));
    }

    public async Task<SlackManagerOwnerWorkflowResult?> IssueOwnerWorkflowAsync(
        string projectId,
        string connectionId,
        string kind,
        CancellationToken ct = default)
    {
        var result = await IssueOwnerWorkflowServiceAsync(projectId, connectionId, kind, ct);
        return result is null
            ? null
            : new(result.ConnectionId, result.BotName, result.ExpiresAt, result.NextAction);
    }

    public async Task<SlackManagerOwnerWorkflowServiceResult?> IssueOwnerWorkflowServiceAsync(
        string projectId,
        string connectionId,
        string kind,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(projectId, connectionId, ct);
        if (connection is null) return null;
        var claim = await _ownerClaims.GenerateAsync(projectId, connectionId, kind, ct: ct);
        return new(
            connection.Id,
            connection.BotName,
            claim.Value,
            claim.ExpiresAt,
            kind == SlackOwnerClaimCodeKinds.Transfer ? "transfer-owner" : "claim-owner");
    }
}

public sealed record SlackManagerConnectionInspection(
    SlackManagerConnectionProjection Connection,
    SlackManagerAppProjection? ManagedApp);

public sealed record SlackManagerAccessPolicyResult(
    SlackManagerConnectionProjection Connection,
    string AccessPolicy,
    IReadOnlyList<string> AllowMembers);

public sealed record SlackManagerOwnerWorkflowResult(
    string ConnectionId,
    string BotName,
    DateTimeOffset ExpiresAt,
    string NextAction);

public sealed record SlackManagerOwnerWorkflowServiceResult(
    string ConnectionId,
    string BotName,
    string Code,
    DateTimeOffset ExpiresAt,
    string NextAction);
