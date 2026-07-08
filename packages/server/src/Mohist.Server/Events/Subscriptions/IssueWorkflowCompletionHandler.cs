using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.workflow.run.completed</c> and dispatches
/// an <see cref="IIssueGrain.CompleteWorkAsync"/> call to the owning
/// issue. The owning issue is recovered directly from the CloudEvent
/// <c>extensions["issueid"]</c> stamp applied at write time by
/// <c>WorkflowRunStore.ToCloudEvent</c>; no scoped service resolution
/// or reverse database lookup is required to identify the target issue.
/// The stamp is the symmetric counterpart of the issue-bound annotation
/// (<c>Annotations["issueId"]</c>) the workflow grain receives at start
/// time from <see cref="IIssueGrain"/>, so the producer (which already
/// knew the binding) propagates it onto the event at write time and the
/// handler reads it back without a second hop.
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
/// Dispatch is synchronous (no background detach): the handler is
/// called from inside a workflow-grain publish path and resolves to a
/// <em>different</em> grain (<see cref="IIssueGrain"/>), so no
/// reentrancy/self-deadlock. This matches the posture of
/// <see cref="EpicAutoDoneHandler"/>, which similarly calls a different
/// grain (<c>EpicGrain</c>) from the issue grain's publish path
/// without detaching.
/// </para>
/// <para>
/// Handler exceptions are swallowed and logged so a dispatch/handling
/// failure never propagates into the workflow-run commit that triggered
/// the event. This matches the best-effort in-memory event delivery
/// model (no outbox, no retry); idempotency is inherited from
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

        if (!evt.Extensions.TryGetValue("issueid", out var issueId)
            || string.IsNullOrWhiteSpace(issueId))
        {
            _log.LogDebug(
                "Workflow-run completed handler: cloud event {EventId} missing issueid extension, skipping ({WorkflowRunId})",
                evt.Id, workflowRunId);
            return;
        }

        try
        {
            var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueId));
            await grain.CompleteWorkAsync(workflowRunId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Workflow-run completed handler: CompleteWorkAsync failed for issue {IssueId} ({WorkflowRunId}); issue is not transitioned by this subscription",
                issueId, workflowRunId);
        }
    }
}