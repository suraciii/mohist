using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Querying;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Issue.WorkflowProfiles;

public sealed class IssueWorkflowCompletionHook : IWorkflowCompletionHook
{
    private readonly IGrainFactory _grains;
    private readonly ProjectQuerier _projectsQuery;
    private readonly IGitService _git;
    private readonly ILogger<IssueWorkflowCompletionHook> _log;

    public IssueWorkflowCompletionHook(IGrainFactory grains, ProjectQuerier projectsQuery, IGitService git, ILogger<IssueWorkflowCompletionHook> log)
    {
        _grains = grains;
        _projectsQuery = projectsQuery;
        _git = git;
        _log = log;
    }

    public async Task OnCompletedAsync(WorkflowCompletionHookContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ProjectId) || string.IsNullOrWhiteSpace(context.IssueId) || context.IssueNumber is null) return;

        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(context.IssueId));
        await issue.CompleteWorkAsync(context.WorkflowRunId);

        var project = await _projectsQuery.GetByIdAsync(context.ProjectId);
        if (project is null)
        {
            _log.LogWarning(
                "Workflow {WorkflowRunId} completed for issue {IssueNumber}, but project {ProjectId} was not found for cleanup",
                context.WorkflowRunId,
                context.IssueNumber.Value,
                context.ProjectId);
            return;
        }

        var cleanup = await _git.RemoveWorktreeAsync(project.Path, project.Name, context.IssueNumber.Value);
        if (cleanup.Status == "failed")
        {
            _log.LogWarning(
                "Failed to remove worktree for completed workflow {WorkflowRunId}: {Message}",
                context.WorkflowRunId,
                cleanup.Message);
        }
    }
}
