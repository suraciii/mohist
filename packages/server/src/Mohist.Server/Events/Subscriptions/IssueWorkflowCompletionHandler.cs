using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.workflow.run.completed</c> and dispatches
/// an <see cref="IIssueGrain.CompleteWorkAsync"/> call to the owning
/// issue. The <c>com.mohist.workflow.run.completed</c> CloudEvent
/// carries no issue context (its payload is empty and the source URI is
/// only the run id), so the owning issue is resolved by a reverse
/// indexed lookup against <see cref="IssueQuerier.GetIssueIdForWorkflowRunAsync"/>,
/// filtered to the in-progress issue bound to that run. The lookup
/// rides the existing indexed <c>IssueRow.WorkflowRunId</c> computed
/// column plus the <c>Status</c> index — no schema change.
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
/// <para>
/// Scoped resolution: the bus wires this handler as a singleton, but
/// <see cref="IssueQuerier"/> is scoped, so the handler opens a fresh
/// <see cref="IServiceScope"/> per delivery via
/// <see cref="IServiceScopeFactory"/>. This is the same pattern
/// <see cref="InboxProjectionHandler"/> uses for the same reason.
/// </para>
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.WorkflowRunCompleted)]
public sealed class IssueWorkflowCompletionHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueWorkflowCompletionHandler> _log;

    public IssueWorkflowCompletionHandler(
        IServiceScopeFactory scopeFactory,
        IGrainFactory grains,
        ILogger<IssueWorkflowCompletionHandler> log)
    {
        _scopeFactory = scopeFactory;
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

        string? issueId;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var querier = scope.ServiceProvider.GetRequiredService<IssueQuerier>();
            issueId = await querier.GetIssueIdForWorkflowRunAsync(workflowRunId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Workflow-run completed handler: reverse lookup failed for {WorkflowRunId} (event {EventId}); issue is not transitioned by this subscription",
                workflowRunId, evt.Id);
            return;
        }

        if (issueId is null)
        {
            _log.LogDebug(
                "Workflow-run completed handler: no in-progress issue bound to {WorkflowRunId} (event {EventId})",
                workflowRunId, evt.Id);
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