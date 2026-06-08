using CloudNative.CloudEvents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

/// <summary>
/// Subscribes to <c>com.mohist.workflow.run.completed</c> on the bus and
/// removes the workflow's worktree. The worktree cleanup is non-idempotent
/// (RemoveWorktreeAsync is safe to repeat but a no-op the second time) and
/// historically lived in the in-grain <c>IssueWorkflowCompletionHook</c> —
/// Step 8 of design/event-mechanism.md moves it to bus-driven execution
/// so the workflow grain no longer holds a reference to issue-domain
/// services.
/// </summary>
public sealed class WorktreeCleanupService : IHostedService
{
    private readonly IEventBus _bus;
    private readonly ProjectQuerier _projectsQuery;
    private readonly IGitService _git;
    private readonly ILogger<WorktreeCleanupService> _log;

    public WorktreeCleanupService(
        IEventBus bus,
        ProjectQuerier projectsQuery,
        IGitService git,
        ILogger<WorktreeCleanupService> log)
    {
        _bus = bus;
        _projectsQuery = projectsQuery;
        _git = git;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Static, permanent subscription. The bus has no
        // Unsubscribe — restart the process to remove it.
        _bus.Subscribe(EventCatalog.ReverseDns.WorkflowRunCompleted, OnWorkflowCompleted);
        _log.LogInformation("WorktreeCleanupService subscribed to {Event}", EventCatalog.ReverseDns.WorkflowRunCompleted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // No subscriptions to tear down. The bus's typed
        // subscriptions are permanent; this method is a
        // no-op kept for the IHostedService contract.
        return Task.CompletedTask;
    }

    private async Task OnWorkflowCompleted(CloudEvent evt)
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
