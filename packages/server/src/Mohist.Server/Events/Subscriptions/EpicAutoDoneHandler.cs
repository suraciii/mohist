using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.issue.completed</c> and dispatches
/// a unified <see cref="IEpicGrain.ReconcileAfterTerminalAsync"/> call
/// to the owning epic. Reconcile covers both the auto-done readiness
/// check and the <c>running</c> epic's next-issue advance.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCompleted)]
public sealed class EpicAutoDoneHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly EpicReconcileDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicAutoDoneHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(scopes, grains, log);
    }

    internal EpicAutoDoneHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "completed", ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.cancelled</c> (cancellation terminal
/// signal) and dispatches the same
/// <see cref="IEpicGrain.ReconcileAfterTerminalAsync"/> call as
/// <see cref="EpicAutoDoneHandler"/>. Both terminal events must trigger
/// reconcile because both clear the serial in-progress slot the epic
/// is waiting on — missing this subscription would deadlock the epic
/// when its in-progress issue is cancelled.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCancelled)]
public sealed class EpicCancelledReconcileHandler : ICloudEventHandler<IssueCancelled>
{
    private readonly EpicReconcileDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicCancelledReconcileHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicCancelledReconcileHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(scopes, grains, log);
    }

    internal EpicCancelledReconcileHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicCancelledReconcileHandler> log)
    {
        _dispatcher = new EpicReconcileDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueCancelled> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCancelled> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "cancelled", ct);
}

/// <summary>
/// Shared dispatch logic for terminal-event → EpicGrain reconcile
/// wiring. Both <see cref="EpicAutoDoneHandler"/> (completed) and
/// <see cref="EpicCancelledReconcileHandler"/> (cancelled) funnel here so the
/// CloudEvent <c>projectid</c>/<c>issueid</c> extension parsing, epic
/// lookup, and grain dispatch stay in one place. Kept
/// package-internal (no <c>public</c> modifier) because this is a
/// wiring concern that should not be consumed outside this folder.
/// </summary>
internal sealed class EpicReconcileDispatcher
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly EpicQuerier? _epicQuerier;
    private readonly IGrainFactory _grains;
    private readonly ILogger _log;

    public EpicReconcileDispatcher(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

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

        string? epicId;
        if (_epicQuerier is not null)
        {
            epicId = await _epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
        }
        else
        {
            await using var scope = _scopes!.CreateAsyncScope();
            var epicQuerier = scope.ServiceProvider.GetRequiredService<EpicQuerier>();
            epicId = await epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
        }
        if (epicId is null)
        {
            return;
        }

        var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epicId}");
        await grain.ReconcileAfterTerminalAsync().ConfigureAwait(false);
    }
}
