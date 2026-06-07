namespace Mohist.Server.Workflow.Services;

/// <summary>
/// Shared context delivered to workflow lifecycle hooks. Carries the
/// minimum needed to identify the workflow and (when applicable) the
/// owning issue.
/// </summary>
public sealed record WorkflowLifecycleHookContext(
    string WorkflowRunId,
    string ProjectId,
    string? IssueId,
    int? IssueNumber,
    string? Reason);

/// <summary>
/// Hook invoked when a workflow run reaches a terminal state. Each terminal
/// state has its own interface so a single consumer can opt into only the
/// states it cares about (e.g. the worktree cleanup only cares about
/// Completed).
/// </summary>
public interface IWorkflowCompletedHook
{
    Task OnCompletedAsync(WorkflowLifecycleHookContext context);
}

public interface IWorkflowFailedHook
{
    Task OnFailedAsync(WorkflowLifecycleHookContext context);
}

public interface IWorkflowStoppedHook
{
    Task OnStoppedAsync(WorkflowLifecycleHookContext context);
}
