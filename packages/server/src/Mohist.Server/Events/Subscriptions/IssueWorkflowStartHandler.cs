using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = EventCatalog.ReverseDns.IssueWorkStarted)]
public sealed class IssueWorkflowStartHandler : ICloudEventHandler<IssueWorkStarted>
{
    private readonly IGrainFactory _grains;
    private readonly IServiceScopeFactory _scopes;

    public IssueWorkflowStartHandler(IGrainFactory grains, IServiceScopeFactory scopes)
    {
        _grains = grains;
        _scopes = scopes;
    }

    public bool Filter(CloudEvent<IssueWorkStarted> evt) => true;

    public async Task HandleAsync(CloudEvent<IssueWorkStarted> evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt, out var context))
        {
            throw new InvalidOperationException($"IssueWorkStarted event '{evt.Id}' has no project-scoped issue number.");
        }

        await using var scope = _scopes.CreateAsyncScope();
        var issues = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        var issue = await issues.LoadAsync(GrainKey.Issue(new IssueKey(context.ProjectId, context.IssueNumber)));
        if (issue is null
            || issue.Status != Mohist.Server.Issue.Domain.IssueStatus.InProgress
            || !string.Equals(issue.WorkflowRunId, evt.Data.WorkflowRunId, StringComparison.Ordinal))
        {
            return;
        }

        var workflow = _grains.GetGrain<IWorkflowGrain>(evt.Data.WorkflowRunId);
        var issueContext = new WorkflowIssueContext(issue.ProjectId, issue.Number, issue.EpicNumber);

        // issue-417 T-006 (D4): when the Issue transaction captured an
        // immutable repository/workspace snapshot, replay it verbatim into
        // the run so dispatch/review/rebase read run-owned facts rather than
        // live Project metadata. A null snapshot (older producer or a path
        // that could not resolve a fingerprint) falls back to the
        // context-only startup.
        if (evt.Data.Repository is { } repository)
        {
            var snapshot = new WorkflowStartSnapshot(
                Repository: new WorkflowRepositoryContext(
                    repository.Name,
                    repository.GitUrl,
                    repository.BaseBranch,
                    repository.RemoteFingerprint,
                    repository.RemoteIdentityVersion),
                Workspace: evt.Data.Workspace is { } workspace
                    ? new WorkspaceIdentity(workspace.Path, workspace.Branch, workspace.ChangeDir)
                    : null);
            await workflow.EnsureStartedAsync(issueContext, snapshot);
        }
        else
        {
            await workflow.EnsureStartedAsync(issueContext);
        }
    }
}
