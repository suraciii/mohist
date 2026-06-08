using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Owns the worktree cleanup side of a completed workflow. The Issue-side
/// transition (CompleteWorkAsync / AbortWorkAsync) is now driven by the
/// bus subscription on <see cref="IIssueGrain"/>; this hook does NOT call
/// those methods. The two paths converge idempotently on the issue grain.
///
/// Worktree cleanup happens once per workflow run and is non-idempotent
/// (RemoveWorktreeAsync is safe to call multiple times but a no-op the
/// second time), so the in-grain hook is the right place for it.
/// </summary>
public sealed class IssueWorkflowCompletionHook : IWorkflowCompletedHook
{
    private readonly ProjectQuerier _projectsQuery;
    private readonly IGitService _git;
    private readonly ILogger<IssueWorkflowCompletionHook> _log;

    public IssueWorkflowCompletionHook(
        ProjectQuerier projectsQuery,
        IGitService git,
        ILogger<IssueWorkflowCompletionHook> log)
    {
        _projectsQuery = projectsQuery;
        _git = git;
        _log = log;
    }

    public async Task OnCompletedAsync(WorkflowLifecycleHookContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId) || context.IssueNumber is null) return;
        await TryRemoveWorktreeAsync(context, context.IssueNumber.Value);
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
