using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

[Subscription(Type = "com.mohist.workflow.run.stopped")]
public sealed class IssueWorkflowStoppedHandler : ICloudEventHandler
{
    private readonly ILogger<IssueWorkflowStoppedHandler> _log;

    public IssueWorkflowStoppedHandler(ILogger<IssueWorkflowStoppedHandler> log)
    {
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var projectId = TryGetExtension(evt, "projectid");
        var issueNumberStr = TryGetExtension(evt, "issueno");
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (projectId is null || issueNumberStr is null || wrId is null) return Task.CompletedTask;

        var reason = TryGetExtension(evt, "reason") ?? "stopped";
        _log.LogInformation(
            "Workflow terminal ({Type}) project={ProjectId} issue={IssueNumber} wrId={WrId} reason={Reason}",
            evt.Type, projectId, issueNumberStr, wrId, reason);
        return Task.CompletedTask;
    }

    private static string? TryGetExtension(CloudEvent evt, string name) =>
        evt.Extensions.TryGetValue(name, out var v) ? v : null;
}

[Subscription(Type = "com.mohist.workflow.run.failed")]
public sealed class IssueWorkflowFailedHandler : ICloudEventHandler
{
    private readonly ILogger<IssueWorkflowFailedHandler> _log;

    public IssueWorkflowFailedHandler(ILogger<IssueWorkflowFailedHandler> log)
    {
        _log = log;
    }

    public bool Filter(CloudEvent evt) => true;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var projectId = TryGetExtension(evt, "projectid");
        var issueNumberStr = TryGetExtension(evt, "issueno");
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (projectId is null || issueNumberStr is null || wrId is null) return Task.CompletedTask;

        var reason = TryGetExtension(evt, "reason") ?? "failed";
        _log.LogInformation(
            "Workflow terminal ({Type}) project={ProjectId} issue={IssueNumber} wrId={WrId} reason={Reason}",
            evt.Type, projectId, issueNumberStr, wrId, reason);
        return Task.CompletedTask;
    }

    private static string? TryGetExtension(CloudEvent evt, string name) =>
        evt.Extensions.TryGetValue(name, out var v) ? v : null;
}
