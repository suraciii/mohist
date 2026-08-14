using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Hosting;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Grain-safe preflight port for a Workflow Agent handoff. The adapter opens a
/// short-lived scope because Agent definition reads remain application-service
/// work; the grain only owns the durable first decision.
/// </summary>
public interface IWorkflowAgentHandoffPreflight
{
    Task<AgentExecutionDefinition?> ResolveAgentAsync(string projectId, string agentRef);
}

public sealed class WorkflowAgentHandoffPreflight(
    IServiceScopeFactory scopeFactory) : IWorkflowAgentHandoffPreflight, ISingletonService
{
    public async Task<AgentExecutionDefinition?> ResolveAgentAsync(string projectId, string agentRef)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentExecutionSnapshotResolver>();
        return await snapshots.ResolveAsync(projectId, agentRef);
    }
}
