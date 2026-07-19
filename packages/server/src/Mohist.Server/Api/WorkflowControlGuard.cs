namespace Mohist.Server.Api;

/// <summary>
/// Shared state-referee for workflow control actions. Used by both the
/// issue-scoped control endpoints (under
/// <c>/api/projects/{projectRef}/issues/{number}/{verb}</c>) and the new
/// workflow-run-scoped control endpoints (under
/// <c>/api/workflow-runs/{workflowRunId}/{verb}</c>) so the two addressing
/// axes share one referee (issue-381 Decision 1).
/// </summary>
public static class WorkflowControlGuard
{
    public static bool IsWorkflowControllableForAction(string? workflowStatus, WorkflowControlAction action) =>
        workflowStatus switch
        {
            "stopped" or "completed" => false,
            "failed" => action is WorkflowControlAction.RetryOrRerun or WorkflowControlAction.Stop,
            null => false,
            _ => true,
        };
}

public enum WorkflowControlAction
{
    ActiveOnly,
    RetryOrRerun,
    Stop,
}
