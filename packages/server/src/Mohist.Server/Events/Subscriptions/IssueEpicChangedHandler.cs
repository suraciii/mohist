using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type = EventCatalog.ReverseDns.IssueEpicChanged)]
public sealed class IssueEpicChangedHandler : ICloudEventHandler<IssueEpicChanged>
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;

    public IssueEpicChangedHandler(IServiceScopeFactory scopes, IGrainFactory grains)
    {
        _scopes = scopes;
        _grains = grains;
    }

    public bool Filter(CloudEvent<IssueEpicChanged> evt) => true;

    public async Task HandleAsync(CloudEvent<IssueEpicChanged> evt, CancellationToken ct)
    {
        if (!evt.Extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId)
            || !evt.Extensions.TryGetValue(EventCatalog.Lineage.Issue, out var issueText)
            || !int.TryParse(issueText, out var issueNumber))
        {
            throw new InvalidOperationException($"IssueEpicChanged event '{evt.Id}' has no project-scoped issue number.");
        }

        await using var scope = _scopes.CreateAsyncScope();
        var issues = scope.ServiceProvider.GetRequiredService<IIssueStore>();
        var issue = await issues.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        if (issue is null) return;

        var epicNumbers = new HashSet<int>();
        Add(epicNumbers, evt.Data.PreviousEpicNumber);
        Add(epicNumbers, evt.Data.EpicNumber);
        Add(epicNumbers, issue.EpicNumber);
        foreach (var epicNumber in epicNumbers)
        {
            var epic = _grains.GetGrain<IEpicGrain>(GrainKey.Epic(new EpicKey(projectId, epicNumber)));
            await epic.RecomputeProgressAsync();
        }

        if (issue.WorkflowRunId is not null)
        {
            var workflow = _grains.GetGrain<IWorkflowGrain>(issue.WorkflowRunId);
            await workflow.RefreshIssueContextAsync(new WorkflowIssueContext(
                issue.ProjectId,
                issue.Number,
                issue.EpicNumber));
        }
    }

    private static void Add(ISet<int> values, int? number)
    {
        if (number is > 0) values.Add(number.Value);
    }
}
