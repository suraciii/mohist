using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class WorktreeCleanupService : IWorkflowRunCompletedHandler
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

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct = default)
    {
        var projectId = TryGetString(evt, "projectid");
        var issueNumberStr = TryGetString(evt, "issueno");
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

    private static string? TryGetString(CloudEvent evt, string name)
    {
        foreach (var (attr, value) in evt.GetPopulatedAttributes())
        {
            if (attr.IsExtension && attr.Name == name && value is not null)
            {
                return value.ToString();
            }
        }
        return null;
    }
}
