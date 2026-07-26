using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Four durable handlers + one dispatcher that turn
/// issue lifecycle events into parent recomputes, plus a fifth handler
/// covering <c>parent-changed</c> (attach/detach fan-out across both
/// affected parents). Every handler reads the <c>parent</c> lineage
/// extension stamped by <see cref="IssueLineage.BuildExtensions"/> on the
/// producing issue event; handlers without a <c>parent</c> extension
/// no-op. Mirrors <see cref="EpicAutoDoneHandler"/>'s shape.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueWorkStarted)]
public sealed class IssueCompositeChildStartedHandler : ICloudEventHandler<IssueWorkStarted>
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueCompositeChildStartedHandler> _log;

    public IssueCompositeChildStartedHandler(
        IGrainFactory grains,
        ILogger<IssueCompositeChildStartedHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueWorkStarted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueWorkStarted> evt, CancellationToken ct) =>
        ParentCompositeRecomputeDispatcher.DispatchAsync(
            evt.Id, evt.Extensions, "work-started", _grains, _log);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.completed</c>:
/// when a child transitions to <c>Done</c>, its parent re-evaluates. A
/// terminal child may also unlock siblings whose prereq it satisfied — the
/// recompute's fan-out starts them. When every child becomes terminal with
/// at least one Done, the parent aggregates to <c>Done</c>.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCompleted)]
public sealed class IssueCompositeChildCompletedHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueCompositeChildCompletedHandler> _log;

    public IssueCompositeChildCompletedHandler(
        IGrainFactory grains,
        ILogger<IssueCompositeChildCompletedHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct) =>
        ParentCompositeRecomputeDispatcher.DispatchAsync(
            evt.Id, evt.Extensions, "completed", _grains, _log);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.cancelled</c>:
/// when a child is cancelled, the parent re-evaluates. A cancellation
/// unfreezes the parent's serial in-progress slot (mirroring the Epic
/// semantics handled by <see cref="EpicCancelledHandler"/>) and the
/// recompute's fan-out starts any Backlog children newly unlocked by
/// losing that blocking sibling.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCancelled)]
public sealed class IssueCompositeChildCancelledHandler : ICloudEventHandler<IssueCancelled>
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueCompositeChildCancelledHandler> _log;

    public IssueCompositeChildCancelledHandler(
        IGrainFactory grains,
        ILogger<IssueCompositeChildCancelledHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueCancelled> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCancelled> evt, CancellationToken ct) =>
        ParentCompositeRecomputeDispatcher.DispatchAsync(
            evt.Id, evt.Extensions, "cancelled", _grains, _log);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.reopened</c>:
/// reopening a Done or Cancelled child returns it to Backlog. A
/// Done parent then auto-flips back to <c>InProgress</c> via the
/// <see cref="IssueCompositeStatusChanged"/> transition. A Cancelled parent
/// stays Cancelled — the user must explicitly reopen the parent. Handlers
/// without a <c>parent</c> lineage extension no-op.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueReopened)]
public sealed class IssueCompositeChildReopenedHandler : ICloudEventHandler<IssueReopened>
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueCompositeChildReopenedHandler> _log;

    public IssueCompositeChildReopenedHandler(
        IGrainFactory grains,
        ILogger<IssueCompositeChildReopenedHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueReopened> evt) => true;

    public Task HandleAsync(CloudEvent<IssueReopened> evt, CancellationToken ct) =>
        ParentCompositeRecomputeDispatcher.DispatchAsync(
            evt.Id, evt.Extensions, "reopened", _grains, _log);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.parent-changed</c>:
/// attaches and detaches both trigger a recompute on the affected parent
/// grain. Attach may unlock the new child for immediate start (handled by
/// <see cref="IIssueGrain.RecomputeCompositeStatusAsync"/>'s fan-out step);
/// detach recomputes the parent's aggregated status against the remaining
/// children and, when the last child is detached, lets the parent revert
/// to a normal issue. Both the previous and new parent are dispatched
/// (one of them is null on attach/detach — those branches no-op).
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueParentChanged)]
public sealed class IssueCompositeParentChangedHandler : ICloudEventHandler<IssueParentChanged>
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<IssueCompositeParentChangedHandler> _log;

    public IssueCompositeParentChangedHandler(
        IGrainFactory grains,
        ILogger<IssueCompositeParentChangedHandler> log)
    {
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent<IssueParentChanged> evt) => true;

    public async Task HandleAsync(CloudEvent<IssueParentChanged> evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt.Extensions, out var issueContext))
        {
            _log.LogDebug(
                "parent-changed event {EventId} missing canonical Issue context; skipping",
                evt.Id);
            return;
        }

        var previousParent = evt.Data.PreviousParentIssueNumber;
        var newParent = evt.Data.ParentIssueNumber;

        if (previousParent is > 0)
        {
            var prevGrain = _grains.GetGrain<IIssueGrain>(
                GrainKey.Issue(new IssueKey(issueContext.ProjectId, previousParent.Value)));
            try
            {
                await prevGrain.RecomputeCompositeStatusAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Recompute on previous parent {ParentNumber} failed for parent-changed event {EventId}",
                    previousParent, evt.Id);
            }
        }

        if (newParent is > 0)
        {
            var nextGrain = _grains.GetGrain<IIssueGrain>(
                GrainKey.Issue(new IssueKey(issueContext.ProjectId, newParent.Value)));
            try
            {
                await nextGrain.RecomputeCompositeStatusAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Recompute on new parent {ParentNumber} failed for parent-changed event {EventId}",
                    newParent, evt.Id);
            }
        }
    }
}

/// <summary>
/// Shared dispatch logic for the four child-event-triggered composite
/// handlers. The CloudEvent carries the producing issue's <c>parent</c>
/// lineage key (stamped by <see cref="IssueLineage.BuildExtensions"/>); the
/// handler reads it directly to find the parent grain and dispatches
/// <see cref="IIssueGrain.RecomputeCompositeStatusAsync"/>. Events
/// without a <c>parent</c> extension no-op. <c>parent-changed</c> does
/// its own dispatch (it routes to two parents) and lives in this file
/// but does not funnel through here.
/// </summary>
internal static class ParentCompositeRecomputeDispatcher
{
    public static async Task DispatchAsync(
        string eventId,
        IReadOnlyDictionary<string, string> extensions,
        string evtType,
        IGrainFactory grains,
        ILogger log)
    {
        if (!CloudEventLineage.TryReadIssueContext(extensions, out var issueContext))
        {
            log.LogDebug(
                "{EvtType} event {EventId} missing canonical Issue context; skipping",
                evtType, eventId);
            return;
        }

        if (!CloudEventLineage.TryReadParent(extensions, out var parentNumber))
        {
            log.LogDebug(
                "{EvtType} event {EventId} has no parent lineage; skipping (event predates parent lineage or issue is unattached)",
                evtType, eventId);
            return;
        }

        var parentGrain = grains.GetGrain<IIssueGrain>(
            GrainKey.Issue(new IssueKey(issueContext.ProjectId, parentNumber)));
        try
        {
            await parentGrain.RecomputeCompositeStatusAsync();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "Recompute on parent {ParentNumber} failed for {EvtType} event {EventId}",
                parentNumber, evtType, eventId);
        }
    }
}
