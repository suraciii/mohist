using CloudNative.CloudEvents;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Issue.Services.WorkflowProfiles;

public sealed class IssueWorkflowAbortedHandler : IWorkflowRunStoppedHandler, IWorkflowRunFailedHandler
{
    private readonly IGrainFactory _grains;
    private readonly IssueIdentityResolver _identityResolver;
    private readonly ILogger<IssueWorkflowAbortedHandler> _log;

    public IssueWorkflowAbortedHandler(
        IGrainFactory grains,
        IssueIdentityResolver identityResolver,
        ILogger<IssueWorkflowAbortedHandler> log)
    {
        _grains = grains;
        _identityResolver = identityResolver;
        _log = log;
    }

    public Task HandleAsync(CloudEvent evt, CancellationToken ct = default)
        => HandleStoppedOrFailedAsync(evt, "aborted", ct);

    Task IWorkflowRunFailedHandler.HandleAsync(CloudEvent evt, CancellationToken ct)
        => HandleStoppedOrFailedAsync(evt, "failed", ct);

    private async Task HandleStoppedOrFailedAsync(CloudEvent evt, string fallbackReason, CancellationToken ct)
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
                "Workflow terminal ({Type}) for project={ProjectId} issue={IssueNumber} wrId={WrId} but issue row not found",
                evt.Type, projectId, issueNumber, wrId);
            return;
        }

        var reason = TryGetExtension(evt, "reason") ?? fallbackReason;

        try
        {
            var issueGrain = _grains.GetGrain<IIssueGrain>(issueId);
            await issueGrain.AbortWorkAsync(wrId, reason);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "IssueWorkflowAbortedHandler failed to abort work for issue={IssueId} wrId={WrId} reason={Reason}",
                issueId, wrId, reason);
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
