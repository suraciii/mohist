using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.issue.work-completed</c> and dispatches
/// a unified <see cref="IEpicGrain.ReconcileAfterTerminalAsync"/> call
/// to the owning epic. Reconcile covers both the auto-done readiness
/// check and the <c>running</c> epic's next-issue advance.
/// </summary>
[Subscription(Type = "com.mohist.issue.work-completed")]
public sealed class EpicAutoDoneHandler : ICloudEventHandler<IssueWorkCompleted>
{
    private readonly EpicReconcileDispatcher _dispatcher;

    public EpicAutoDoneHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueWorkCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueWorkCompleted> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "work-completed", ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.closed</c> (cancellation terminal
/// signal) and dispatches the same
/// <see cref="IEpicGrain.ReconcileAfterTerminalAsync"/> call as
/// <see cref="EpicAutoDoneHandler"/>. Both terminal events must trigger
/// reconcile because both clear the serial in-progress slot the epic
/// is waiting on — missing this subscription would deadlock the epic
/// when its in-progress issue is cancelled.
/// </summary>
[Subscription(Type = "com.mohist.issue.closed")]
public sealed class EpicClosedReconcileHandler : ICloudEventHandler<IssueClosed>
{
    private readonly EpicReconcileDispatcher _dispatcher;

    public EpicClosedReconcileHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicClosedReconcileHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueClosed> evt) => true;

    public Task HandleAsync(CloudEvent<IssueClosed> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "closed", ct);
}

/// <summary>
/// Shared dispatch logic for terminal-event → EpicGrain reconcile
/// wiring. Both <see cref="EpicAutoDoneHandler"/> (work-completed) and
/// <see cref="EpicClosedReconcileHandler"/> (closed) funnel here so the
/// CloudEvent <c>projectid</c>/<c>issueid</c> extension parsing, epic
/// lookup, and exception swallowing stay in one place. Kept
/// package-internal (no <c>public</c> modifier) because this is a
/// wiring concern that should not be consumed outside this folder.
/// </summary>
internal sealed class EpicReconcileDispatcher
{
    private readonly EpicQuerier _epicQuerier;
    private readonly IGrainFactory _grains;
    private readonly ILogger _log;

    public EpicReconcileDispatcher(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger log)
    {
        _epicQuerier = epicQuerier;
        _grains = grains;
        _log = log;
    }

    public async Task DispatchAsync(
        string eventId,
        IReadOnlyDictionary<string, string> extensions,
        string evtType,
        CancellationToken ct)
    {
        if (!extensions.TryGetValue("projectid", out var projectId) || string.IsNullOrWhiteSpace(projectId))
        {
            _log.LogDebug(
                "{EvtType} event missing projectid extension; skipping (event {EventId})",
                evtType, eventId);
            return;
        }
        if (!extensions.TryGetValue("issueid", out var issueId) || string.IsNullOrWhiteSpace(issueId))
        {
            _log.LogDebug(
                "{EvtType} event missing issueid extension; skipping (event {EventId})",
                evtType, eventId);
            return;
        }

        var epicId = await _epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
        if (epicId is null)
        {
            return;
        }

        try
        {
            var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epicId}");
            await grain.ReconcileAfterTerminalAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Epic reconcile-on-terminal handler failed for project {ProjectId} epic {EpicId} issue {IssueId} ({EvtType}); relying on reconciliation sweep",
                projectId, epicId, issueId, evtType);
        }
    }
}