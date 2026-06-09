using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class IssueWorkflowCompletionHandler : IWorkflowRunCompletedHandler
{
    private readonly IGrainFactory _grains;
    private readonly IssueIdentityResolver _identityResolver;
    private readonly ILogger<IssueWorkflowCompletionHandler> _log;

    public IssueWorkflowCompletionHandler(
        IGrainFactory grains,
        IssueIdentityResolver identityResolver,
        ILogger<IssueWorkflowCompletionHandler> log)
    {
        _grains = grains;
        _identityResolver = identityResolver;
        _log = log;
    }

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct = default)
    {
        var projectId = TryGetExtension(evt, "projectid");
        var issueNumberStr = TryGetExtension(evt, "issueno");
        var wrId = TryGetExtension(evt, "workflowrunid");
        if (projectId is null || issueNumberStr is null || wrId is null) return;
        if (!int.TryParse(issueNumberStr, out var issueNumber)) return;

        var issueId = await _identityResolver.GetIdAsync(projectId, issueNumber, ct);
        if (issueId is null)
        {
            _log.LogDebug(
                "WorkflowRunCompleted for project={ProjectId} issue={IssueNumber} wrId={WrId} but issue row not found",
                projectId, issueNumber, wrId);
            return;
        }

        try
        {
            var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
            await issueGrain.CompleteWorkAsync(wrId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "IssueWorkflowCompletionHandler failed to complete work for issue={IssueId} wrId={WrId}",
                issueId, wrId);
        }
    }

    private static string? TryGetExtension(CloudEvent evt, string name)
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
