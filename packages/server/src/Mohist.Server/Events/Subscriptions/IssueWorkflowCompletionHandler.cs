using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.workflow.run.completed</c> and dispatches
/// an <see cref="IIssueGrain.CompleteWorkAsync"/> call to the owning
/// issue. The owning issue is recovered directly from the CloudEvent
/// <c>extensions["projectid"]</c> and <c>extensions["issue"]</c>
/// stamps applied at write time by <c>WorkflowRunStore.ToCloudEvent</c>;
/// no scoped service resolution or reverse database lookup is required to
/// identify the target issue. The workflow's local context carries the
/// same project-scoped Issue number from its durable start command.
/// <para>
/// This is the symmetric counterpart of
/// <see cref="EpicAutoDoneHandler"/> (issue→epic): a terminal
/// workflow-run event drives its owning issue to <c>Done</c> through
/// the IssueGrain, instead of an issue completion event driving the
/// owning epic through the EpicGrain. Only <c>Completed</c> is handled —
/// the single-type <c>[Subscription]</c> covers the explicit
/// "only Completed drives the transition" rule (<c>failed</c>/<c>stopped</c>
/// terminal states are out of scope for this change and remain
/// unchanged).
/// </para>
/// <para>
/// Handler failures propagate to the durable dispatcher, which owns retry
/// and dead-letter policy. Idempotency is inherited from
/// <see cref="IIssueGrain.CompleteWorkAsync"/> via its
/// <c>Status == InProgress</c> and <c>workflowRunId</c> match guards.
/// </para>
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.WorkflowRunCompleted)]
public sealed class IssueWorkflowCompletionHandler : ICloudEventHandler
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueWorkflowCompletionHandler> _log;

    public IssueWorkflowCompletionHandler(
        IGrainFactory grains,
        ILogger<IssueWorkflowCompletionHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.WorkflowRunCompleted, StringComparison.Ordinal);

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var (workflowRunId, _) = WorkflowEventSerializer.ExtractContextFromSource(evt.Source.ToString());
        if (string.IsNullOrWhiteSpace(workflowRunId))
        {
            _log.LogDebug(
                "Workflow-run completed handler: cloud event {EventId} has empty source, skipping",
                evt.Id);
            return;
        }

        if (!evt.Extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId)
            || !evt.Extensions.TryGetValue(EventCatalog.Lineage.Issue, out var issueNumberText)
            || !int.TryParse(issueNumberText, out var issueNumber))
        {
            _log.LogDebug(
                "Workflow-run completed handler: cloud event {EventId} missing project-scoped issue extension, skipping ({WorkflowRunId})",
                evt.Id, workflowRunId);
            return;
        }

        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.CompleteWorkAsync(workflowRunId).ConfigureAwait(false);
    }
}
