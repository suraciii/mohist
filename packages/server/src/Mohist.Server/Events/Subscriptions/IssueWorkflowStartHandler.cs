using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
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
        if (!evt.Extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId)
            || !evt.Extensions.TryGetValue(EventCatalog.Lineage.Issue, out var issueNumberText)
            || !int.TryParse(issueNumberText, out var issueNumber))
        {
            throw new InvalidOperationException($"IssueWorkStarted event '{evt.Id}' has no project-scoped issue number.");
        }

        await using var scope = _scopes.CreateAsyncScope();
        var issues = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        var issue = await issues.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        if (issue is null
            || issue.Status != Mohist.Server.Issue.Domain.IssueStatus.InProgress
            || !string.Equals(issue.WorkflowRunId, evt.Data.WorkflowRunId, StringComparison.Ordinal))
        {
            return;
        }

        var workflow = _grains.GetGrain<IWorkflowGrain>(evt.Data.WorkflowRunId);
        await workflow.EnsureStartedAsync(new WorkflowIssueContext(
            issue.ProjectId,
            issue.Number,
            issue.EpicNumber));
    }
}
