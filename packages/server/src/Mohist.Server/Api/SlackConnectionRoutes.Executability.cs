using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;

namespace Mohist.Server.Api;

public static partial class SlackConnectionRoutes
{
    private static async Task<AgentConnectionDispatchDecision> ResolveInboundDispatchDecisionAsync(
        IServiceProvider services,
        string projectId,
        AgentInfo agent,
        CancellationToken ct)
    {
        var executability = await services.GetRequiredService<AgentReadinessService>()
            .GetAsync(projectId, agent, ct);
        return AgentConnectionDispatchDecision.For(executability.State);
    }
}
