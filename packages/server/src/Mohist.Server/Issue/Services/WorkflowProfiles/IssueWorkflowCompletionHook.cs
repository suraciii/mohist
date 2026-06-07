using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Reacts to every workflow terminal state on behalf of the owning issue.
/// Completed: mark the issue Done and remove the worktree.
/// Failed / Stopped: abort the issue (transition to Cancelled) so it does
/// not stay stuck in InProgress.
/// </summary>
public sealed class IssueWorkflowCompletionHook :
    IWorkflowCompletedHook,
    IWorkflowFailedHook,
    IWorkflowStoppedHook
{
    private readonly IGrainFactory _grains;
    private readonly ProjectQuerier _projectsQuery;
    private readonly IGitService _git;
    private readonly ILogger<IssueWorkflowCompletionHook> _log;

    public IssueWorkflowCompletionHook(
        IGrainFactory grains,
        ProjectQuerier projectsQuery,
        IGitService git,
        ILogger<IssueWorkflowCompletionHook> log)
    {
        _grains = grains;
        _projectsQuery = projectsQuery;
        _git = git;
        _log = log;
    }

    public async Task OnCompletedAsync(WorkflowLifecycleHookContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId) || string.IsNullOrWhiteSpace(context.IssueId) || context.IssueNumber is null) return;

        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(context.IssueId));
        await issue.CompleteWorkAsync(context.WorkflowRunId);

        await TryRemoveWorktreeAsync(context, context.IssueNumber.Value);
    }

    public Task OnFailedAsync(WorkflowLifecycleHookContext context)
    {
        if (string.IsNullOrWhiteSpace(context.IssueId)) return Task.CompletedTask;
        // Fire-and-forget: IssueWorkflowCompletionHook runs synchronously from
        // WorkflowGrain.On; an `await issue.AbortWorkAsync` here would deadlock
        // when the caller is IssueGrain itself (e.g. /cancel → StopAsync → hook
        // → AbortWorkAsync on the busy IssueGrain). The AbortWorkflow transition
        // is idempotent and the user's CancelAsync already calls Issue.Close,
        // so dropping the await is safe.
        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(context.IssueId));
        _ = issue.AbortWorkAsync(context.WorkflowRunId, context.Reason);
        return Task.CompletedTask;
    }

    public Task OnStoppedAsync(WorkflowLifecycleHookContext context)
    {
        if (string.IsNullOrWhiteSpace(context.IssueId)) return Task.CompletedTask;
        // See OnFailedAsync for the fire-and-forget rationale.
        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(context.IssueId));
        _ = issue.AbortWorkAsync(context.WorkflowRunId, context.Reason);
        return Task.CompletedTask;
    }

    private async Task TryRemoveWorktreeAsync(WorkflowLifecycleHookContext context, int issueNumber)
    {
        var project = await _projectsQuery.GetByIdAsync(context.ProjectId);
        if (project is null)
        {
            _log.LogWarning(
                "Workflow {WorkflowRunId} completed for issue {IssueNumber}, but project {ProjectId} was not found for cleanup",
                context.WorkflowRunId,
                issueNumber,
                context.ProjectId);
            return;
        }

        var cleanup = await _git.RemoveWorktreeAsync(project.Path, project.Name, issueNumber);
        if (cleanup.Status == "failed")
        {
            _log.LogWarning(
                "Failed to remove worktree for completed workflow {WorkflowRunId}: {Message}",
                context.WorkflowRunId,
                cleanup.Message);
        }
    }
}
