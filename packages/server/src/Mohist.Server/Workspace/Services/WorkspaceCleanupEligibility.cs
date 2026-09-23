using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workspace.Domain;

namespace Mohist.Server.Workspace.Services;

public static class WorkspaceCleanupEligibility
{
    public static async Task<WorkspaceCleanupDecision> CheckAsync(
        WorkspaceState state,
        WorkspaceQuerier querier,
        IGrainFactory grains,
        CancellationToken ct = default)
    {
        var active = await querier.CountActiveBoundSessionsAsync(state.ProjectId, state.Name, ct);
        if (active > 0)
            return new(false, "workspace_in_use", active);

        if (state.Origin is WorkspaceOrigin.Issue issue)
        {
            var workflow = await grains.GetGrain<IIssueGrain>(
                GrainKey.Issue(new IssueKey(state.ProjectId, issue.IssueNumber)))
                .GetWorkflowStatusAsync();
            if (workflow?.WorkflowRunId is not null)
            {
                var status = workflow.Workflow?.Status;
                if (status is null || !string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status, "Stopped", StringComparison.OrdinalIgnoreCase))
                    return new(false, "workflow_reserves_workspace", active);
            }
        }

        return new(true, null, active);
    }
}

public sealed record WorkspaceCleanupDecision(bool Reclaimable, string? Reason, int ActiveBoundSessions);
