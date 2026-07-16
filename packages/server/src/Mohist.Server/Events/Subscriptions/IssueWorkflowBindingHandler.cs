using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = EventCatalog.ReverseDns.IssueWorkStarted)]
public sealed class IssueWorkflowBindingHandler : ICloudEventHandler<IssueWorkStarted>
{
    private readonly IGrainFactory _grains;

    public IssueWorkflowBindingHandler(IGrainFactory grains)
    {
        _grains = grains;
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

        var issue = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await issue.EnsureWorkflowBindingAsync(evt.Data.WorkflowRunId);
    }
}
