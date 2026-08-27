using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Agent.Domain;
using Mohist.Server.Agent.Services;
using Mohist.Server.Api;
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
    Task<WorkflowAgentPreflightResult> ResolveAgentAsync(string projectId, string agentRef);
}

public sealed record WorkflowAgentPreflightResult(
    AgentExecutionIdentitySnapshot? Agent,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class WorkflowAgentHandoffPreflight(
    IServiceScopeFactory scopeFactory) : IWorkflowAgentHandoffPreflight, ISingletonService
{
    public async Task<WorkflowAgentPreflightResult> ResolveAgentAsync(string projectId, string agentRef)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentExecutionIdentitySnapshotResolver>();
        var snapshot = await snapshots.ResolveWithIdentityAsync(projectId, agentRef);
        if (snapshot is not null)
            return new WorkflowAgentPreflightResult(snapshot);

        var agents = scope.ServiceProvider.GetRequiredService<AgentQuerier>();
        var agent = await AgentRefResolver.ResolveAsync(agents, projectId, agentRef);
        if (agent is null
            && !agentRef.StartsWith("agent_", StringComparison.Ordinal)
            && BuiltInAgentCatalog.Find(agentRef) is not null)
        {
            agent = BuiltInAgentCatalog.Resolve(agentRef, projectId);
        }
        if (agent is null || agent.Status != AgentStatus.Active)
        {
            return new WorkflowAgentPreflightResult(
                null,
                "agent_not_found",
                $"Workflow Agent handoff references Agent '{agentRef}' which does not exist or is archived.");
        }

        var detail = agent.Executability?.PendingLaunchNote;
        if (string.IsNullOrWhiteSpace(detail) && agent.Executability?.Gaps is { Count: > 0 } gaps)
            detail = string.Join("; ", gaps.Select(gap => gap.Message));
        return new WorkflowAgentPreflightResult(
            null,
            "agent_not_ready",
            string.IsNullOrWhiteSpace(detail)
                ? $"Workflow Agent handoff references Agent '{agentRef}' which is not ready to execute."
                : $"Workflow Agent handoff references Agent '{agentRef}' which is not ready to execute: {detail}");
    }
}
