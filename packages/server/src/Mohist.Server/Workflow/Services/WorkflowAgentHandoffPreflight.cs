using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Grains;
using Orleans;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Grain-safe preflight port for a Workflow Agent handoff. The adapter opens a
/// short-lived scope because Agent definition reads remain application-service
/// work; the grain only owns the durable first decision.
/// </summary>
public interface IWorkflowAgentHandoffPreflight
{
    Task<WorkflowAgentHandoffAgent?> ResolveAgentAsync(string projectId, string agentRef);
}

[GenerateSerializer]
public sealed record WorkflowAgentHandoffAgent(
    [property: Id(0)] string AgentId,
    [property: Id(1)] AgentExecutionDefinition ExecutionDefinition);

public sealed class WorkflowAgentHandoffPreflight(
    IServiceScopeFactory scopeFactory) : IWorkflowAgentHandoffPreflight, ISingletonService
{
    public async Task<WorkflowAgentHandoffAgent?> ResolveAgentAsync(string projectId, string agentRef)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentExecutionSnapshotResolver>();
        var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
        if (agent is null)
            return null;

        var definition = await snapshots.ResolveAsync(projectId, agent.Id);
        return definition is null
            ? null
            : new WorkflowAgentHandoffAgent(agent.Id, definition);
    }
}
