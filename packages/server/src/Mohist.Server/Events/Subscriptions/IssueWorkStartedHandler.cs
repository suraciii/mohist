using System.Text.Json;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = EventCatalog.ReverseDns.IssueWorkStarted)]
public sealed class IssueWorkStartedHandler : ICloudEventHandler
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueWorkStartedHandler> _log;

    public IssueWorkStartedHandler(
        IGrainFactory grains,
        ILogger<IssueWorkStartedHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.IssueWorkStarted, StringComparison.Ordinal);

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!evt.Extensions.TryGetValue("issueid", out var issueId)
            || string.IsNullOrWhiteSpace(issueId)
            || !evt.Data.HasValue)
        {
            return;
        }

        var started = evt.Data.Value.Deserialize<IssueWorkStarted>(JSON.Options);
        if (started?.Repository is null || started.Workspace is null || started.Context is null)
        {
            _log.LogWarning("IssueWorkStarted event {EventId} has no complete start snapshot", evt.Id);
            return;
        }

        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
        var activeRunId = await issue.GetActiveWorkflowRunIdAsync().ConfigureAwait(false);
        if (!string.Equals(activeRunId, started.WorkflowRunId, StringComparison.Ordinal))
        {
            _log.LogDebug(
                "Ignoring stale IssueWorkStarted event {EventId} for run {EventRunId}; active run is {ActiveRunId}",
                evt.Id,
                started.WorkflowRunId,
                activeRunId);
            return;
        }

        var context = started.Context;
        if (!string.Equals(context.IssueId, issueId, StringComparison.Ordinal))
            throw new InvalidOperationException($"IssueWorkStarted event {evt.Id} has mismatched issue context");

        var workflow = _grains.GetGrain<IWorkflowGrain>(started.WorkflowRunId);
        await workflow.EnsureStartedAsync(new WorkflowStartInput(
            Metadata: new WorkflowRunMetadata(
                Name: null,
                CreatedAt: evt.Time,
                Annotations: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["projectId"] = context.ProjectId,
                    ["issueId"] = context.IssueId,
                    ["issueNumber"] = context.IssueNumber.ToString(),
                }),
            Workspace: new WorkspaceIdentity(
                started.Workspace.Path,
                started.Workspace.Branch,
                started.Workspace.ChangeDir),
            Repository: new WorkflowRepositoryContext(
                started.Repository.Name,
                started.Repository.GitUrl,
                started.Repository.BaseBranch,
                started.Repository.RemoteFingerprint,
                started.Repository.RemoteIdentityVersion))).ConfigureAwait(false);
    }
}
