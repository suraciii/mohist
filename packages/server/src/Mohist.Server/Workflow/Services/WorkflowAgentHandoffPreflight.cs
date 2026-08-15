using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Grain-safe preflight port for a Workflow Agent handoff. The adapter opens a
/// short-lived scope because Agent definition and WorkflowRun reads remain
/// application-service work; the grain only owns the durable first decision.
/// Both resolutions run at first preflight only — the frozen plan replays
/// without re-reading mutable Agent or run configuration.
/// </summary>
public interface IWorkflowAgentHandoffPreflight
{
    /// <summary>
    /// Resolves the Agent identity and generic execution definition in one
    /// read. Null when the Agent does not resolve or is not active.
    /// </summary>
    Task<WorkflowAgentHandoffAgentSnapshot?> ResolveAgentAsync(string projectId, string agentRef);

    /// <summary>
    /// Resolves the run-scoped execution context (issue/epic lineage and
    /// workspace binding) from the WorkflowRun snapshot. Null when the run
    /// cannot be loaded — an unbound handoff carries no run context.
    /// </summary>
    Task<WorkflowAgentHandoffRunContext?> ResolveRunContextAsync(string workflowRunId);
}

/// <summary>
/// Agent identity next to the execution definition the preflight freezes.
/// Resolved in a single snapshot read so a frozen plan never mixes an old
/// identity with a new definition.
/// </summary>
public sealed record WorkflowAgentHandoffAgentSnapshot(
    string AgentId,
    string AgentName,
    AgentExecutionDefinition Definition);

public sealed class WorkflowAgentHandoffPreflight(
    IServiceScopeFactory scopeFactory) : IWorkflowAgentHandoffPreflight, ISingletonService
{
    public async Task<WorkflowAgentHandoffAgentSnapshot?> ResolveAgentAsync(string projectId, string agentRef)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<IAgentExecutionSnapshotResolver>();
        var snapshot = await snapshots.ResolveSnapshotAsync(projectId, agentRef);
        return snapshot is null
            ? null
            : new WorkflowAgentHandoffAgentSnapshot(
                snapshot.AgentId,
                snapshot.AgentName,
                snapshot.Definition);
    }

    public async Task<WorkflowAgentHandoffRunContext?> ResolveRunContextAsync(string workflowRunId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
        var run = await runs.LoadAsync(workflowRunId);
        return run is null ? null : RunContextFor(run);
    }

    /// <summary>
    /// Workspace binding mirrors the inline dispatch renderer: an issue-linked
    /// run executes in the named <c>issue-{n}</c> workspace, a generic run in
    /// its free-form workspace path, and neither fact is re-read after the
    /// freeze.
    /// </summary>
    private static WorkflowAgentHandoffRunContext RunContextFor(WorkflowRun run)
    {
        WorkflowAgentHandoffWorkspace? workspace;
        if (run.Metadata.IssueNumber is { } issueNumber)
        {
            workspace = new WorkflowAgentHandoffWorkspace(
                Name: $"issue-{issueNumber}",
                Path: null,
                Branch: null);
        }
        else if (run.Workspace is { } identity)
        {
            workspace = new WorkflowAgentHandoffWorkspace(
                Name: null,
                Path: identity.Path,
                Branch: identity.Branch);
        }
        else
        {
            workspace = null;
        }

        return new WorkflowAgentHandoffRunContext(
            IssueNumber: run.Metadata.IssueNumber,
            EpicNumber: run.Metadata.EpicNumber,
            Workspace: workspace);
    }
}
