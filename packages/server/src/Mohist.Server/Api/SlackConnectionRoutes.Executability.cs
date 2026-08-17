using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private sealed record SlackLaunchAdmission(
        AgentInfo? Agent,
        AgentConnectionDispatchDecision Decision);

    private static async Task<AgentConnectionDispatchDecision> ResolveInboundDispatchDecisionAsync(
        IServiceProvider services,
        string projectId,
        AgentInfo agent,
        CancellationToken ct)
    {
        var executability = await services.GetRequiredService<AgentReadinessService>()
            .GetAsync(projectId, agent, ct);
        return AgentConnectionDispatchDecision.For(executability);
    }

    private static async Task<SlackLaunchAdmission> ResolveNewLaunchAdmissionAsync(
        IServiceProvider services,
        string projectId,
        AgentConnection connection,
        AgentQuerier agents,
        CancellationToken ct)
    {
        var connectionDecision = AgentConnectionDispatchDecision.ForConnection(connection);
        if (!connectionDecision.Accepted)
            return new(null, connectionDecision);

        var agent = await agents.GetByIdAsync(projectId, connection.AgentId, ct);
        if (agent is null)
        {
            return new(
                null,
                new(
                    false,
                    "agent_not_found",
                    "The Agent bound to this Connection no longer exists."));
        }

        return new(
            agent,
            await ResolveInboundDispatchDecisionAsync(services, projectId, agent, ct));
    }
}
