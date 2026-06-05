namespace Mohist.Server.Workflow.Services;

public interface IWorkflowCompletionHook
{
    Task OnCompletedAsync(WorkflowCompletionHookContext context);
}

public sealed record WorkflowCompletionHookContext(
    string WorkflowRunId,
    string ProjectId,
    string? IssueId,
    int? IssueNumber);
