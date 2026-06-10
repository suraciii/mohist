using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

[Subscription(Type = "com.mohist.workflow.run.completed")]
public sealed class WorktreeCleanupService : ICloudEventHandler<WorkflowRunCompleted>
{
    private readonly ProjectQuerier _projectsQuery;
    private readonly IGitService _git;
    private readonly ILogger<WorktreeCleanupService> _log;

    public WorktreeCleanupService(
        ProjectQuerier projectsQuery,
        IGitService git,
        ILogger<WorktreeCleanupService> log)
    {
        _projectsQuery = projectsQuery;
        _git = git;
        _log = log;
    }

    public bool Filter(CloudEvent<WorkflowRunCompleted> evt) => true;

    public async Task HandleAsync(CloudEvent<WorkflowRunCompleted> evt, CancellationToken ct)
    {
        var projectId = TryGetExtension(evt, "projectid");
        var issueNumberStr = TryGetExtension(evt, "issueno");
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(issueNumberStr)) return;
        if (!int.TryParse(issueNumberStr, out var issueNumber)) return;

        try
        {
            var project = await _projectsQuery.GetByIdAsync(projectId);
            if (project is null)
            {
                _log.LogWarning(
                    "Workflow completed for issue {IssueNumber}, but project {ProjectId} was not found for worktree cleanup",
                    issueNumber, projectId);
                return;
            }

            var cleanup = await _git.RemoveWorktreeAsync(project.Path, project.Name, issueNumber);
            if (cleanup.Status == "failed")
            {
                _log.LogWarning(
                    "Failed to remove worktree for issue {IssueNumber}: {Message}",
                    issueNumber, cleanup.Message);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WorktreeCleanupService failed for issue {IssueNumber}", issueNumber);
        }
    }

    private static string? TryGetExtension<TData>(CloudEvent<TData> evt, string name) where TData : class =>
        evt.Extensions.TryGetValue(name, out var v) ? v : null;
}
