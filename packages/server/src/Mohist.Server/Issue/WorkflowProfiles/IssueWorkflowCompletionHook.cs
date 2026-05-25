using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workflow.Hooks;
using Mohist.Server.Workspace;

namespace Mohist.Server.Issue.WorkflowProfiles;

public sealed class IssueWorkflowCompletionHook : IWorkflowCompletionHook
{
    private readonly IGrainFactory _grains;
    private readonly IGitService _git;
    private readonly ILogger<IssueWorkflowCompletionHook> _log;

    public IssueWorkflowCompletionHook(IGrainFactory grains, IGitService git, ILogger<IssueWorkflowCompletionHook> log)
    {
        _grains = grains;
        _git = git;
        _log = log;
    }

    public async Task OnCompletedAsync(WorkflowCompletionHookContext context)
    {
        var correlation = context.Correlation;
        if (correlation?.OwnerType != "issue" ||
            string.IsNullOrWhiteSpace(correlation.ProjectId) ||
            correlation.OwnerNumber is null)
        {
            return;
        }

        var issue = _grains.GetGrain<IIssueGrain>($"{correlation.ProjectId}:{correlation.OwnerNumber.Value}");
        await issue.CompleteWorkflowAsync(context.WorkflowRunId);

        var projectGrain = _grains.GetGrain<IProjectGrain>("default");
        var project = await projectGrain.GetByIdAsync(correlation.ProjectId);
        if (project is null)
        {
            _log.LogWarning(
                "Workflow {WorkflowRunId} completed for issue {IssueNumber}, but project {ProjectId} was not found for cleanup",
                context.WorkflowRunId,
                correlation.OwnerNumber.Value,
                correlation.ProjectId);
            return;
        }

        var cleanup = await _git.RemoveWorktreeAsync(project.Path, project.Name, correlation.OwnerNumber.Value);
        if (cleanup.Status == "failed")
        {
            _log.LogWarning(
                "Failed to remove worktree for completed workflow {WorkflowRunId}: {Message}",
                context.WorkflowRunId,
                cleanup.Message);
        }
    }
}
