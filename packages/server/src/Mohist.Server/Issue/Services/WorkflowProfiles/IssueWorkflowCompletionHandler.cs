using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

[Subscription(Type = "com.mohist.workflow.run.completed")]
public sealed class IssueWorkflowCompletionHandler : ICloudEventHandler
{
    private readonly ILogger<IssueWorkflowCompletionHandler> _log;

    public IssueWorkflowCompletionHandler(ILogger<IssueWorkflowCompletionHandler> log)
    {
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var projectId = TryGetExtension(evt, "projectid");
        var issueNumberStr = TryGetExtension(evt, "issueno");
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (projectId is null || issueNumberStr is null || wrId is null)
            return Task.CompletedTask;

        _log.LogInformation(
            "WorkflowRunCompleted project={ProjectId} issue={IssueNumber} wrId={WrId}",
            projectId, issueNumberStr, wrId);
        return Task.CompletedTask;
    }

    private static string? TryGetExtension(CloudEvent evt, string name) =>
        evt.Extensions.TryGetValue(name, out var v) ? v : null;
}
