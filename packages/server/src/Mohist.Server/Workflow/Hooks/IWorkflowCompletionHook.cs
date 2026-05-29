using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Workflow.Hooks;

public interface IWorkflowCompletionHook
{
    Task OnCompletedAsync(WorkflowCompletionHookContext context);
}

public sealed record WorkflowCompletionHookContext(
    string WorkflowRunId,
    string ProjectId,
    int? IssueNumber,
    WorkflowStatusSnapshot CompletedStatus);