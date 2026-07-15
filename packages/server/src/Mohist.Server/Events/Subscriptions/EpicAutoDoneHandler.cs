using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Epic;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Subscribes to <c>com.mohist.issue.completed</c> and dispatches
/// a unified <see cref="IEpicGrain.RecomputeProgressAsync"/> call
/// to the owning epic. Recompute progress covers both the auto-done
/// readiness check and the <c>running</c> epic's next-issue advance.
/// Also reverse-looks-up epics whose members list the completed issue
/// as an external prerequisite, so a dependent epic can advance once
/// the blocker clears.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCompleted)]
public sealed class EpicAutoDoneHandler : ICloudEventHandler<IssueCompleted>
{
    private readonly EpicProgressRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicAutoDoneHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(scopes, grains, log);
    }

    internal EpicAutoDoneHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicAutoDoneHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueCompleted> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCompleted> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "completed", includePrerequisiteLookup: true, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.cancelled</c> (cancellation terminal
/// signal) and dispatches the same
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> call as
/// <see cref="EpicAutoDoneHandler"/>. Both terminal events must trigger
/// recompute progress because both clear the serial in-progress slot the
/// epic is waiting on — missing this subscription would deadlock the
/// epic when its in-progress issue is cancelled. Cancellation only recomputes
/// the owning epic: external prerequisites become startable only when done.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueCancelled)]
public sealed class EpicCancelledHandler : ICloudEventHandler<IssueCancelled>
{
    private readonly EpicProgressRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicCancelledHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicCancelledHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(scopes, grains, log);
    }

    internal EpicCancelledHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicCancelledHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueCancelled> evt) => true;

    public Task HandleAsync(CloudEvent<IssueCancelled> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "cancelled", includePrerequisiteLookup: false, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.draft-changed</c> and triggers
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> on the owning epic
/// when a member transitions from draft to ready (undraft). A draft
/// member blocks <c>SelectStartableNext</c>; clearing that blocker may
/// unblock a running-but-idle epic. Only undraft (NewIsDraft == false)
/// is actionable — drafting a ready issue has no epic-progress effect.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueDraftChanged)]
public sealed class EpicDraftChangedHandler : ICloudEventHandler<IssueDraftChanged>
{
    private readonly EpicProgressRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicDraftChangedHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicDraftChangedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(scopes, grains, log);
    }

    internal EpicDraftChangedHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicDraftChangedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueDraftChanged> evt) => !evt.Data.NewIsDraft;

    public Task HandleAsync(CloudEvent<IssueDraftChanged> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "draft-changed", includePrerequisiteLookup: false, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.reopened</c> and triggers
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> on the owning epic.
/// Reopening a cancelled member returns it to backlog, potentially making
/// it startable in a running-but-idle epic. This readiness transition was
/// a convergence path the deleted sweep covered; this subscription closes
/// the gap with a durable, event-driven trigger.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssueReopened)]
public sealed class EpicIssueReopenedHandler : ICloudEventHandler<IssueReopened>
{
    private readonly EpicProgressRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicIssueReopenedHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicIssueReopenedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(scopes, grains, log);
    }

    internal EpicIssueReopenedHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicIssueReopenedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssueReopened> evt) => true;

    public Task HandleAsync(CloudEvent<IssueReopened> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "reopened", includePrerequisiteLookup: false, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.issue.prerequisite-removed</c> and triggers
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> on the owning epic.
/// Removing a prerequisite can make a previously-blocked backlog member
/// startable in a running-but-idle epic — a readiness transition the
/// deleted periodic sweep used to converge. This subscription closes that
/// gap with a durable, event-driven trigger.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.IssuePrerequisiteRemoved)]
public sealed class EpicPrerequisiteRemovedHandler : ICloudEventHandler<IssuePrerequisiteRemoved>
{
    private readonly EpicProgressRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicPrerequisiteRemovedHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<EpicPrerequisiteRemovedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(scopes, grains, log);
    }

    internal EpicPrerequisiteRemovedHandler(
        EpicQuerier epicQuerier,
        IGrainFactory grains,
        ILogger<EpicPrerequisiteRemovedHandler> log)
    {
        _dispatcher = new EpicProgressRecomputeDispatcher(epicQuerier, grains, log);
    }

    public bool Filter(CloudEvent<IssuePrerequisiteRemoved> evt) => true;

    public Task HandleAsync(CloudEvent<IssuePrerequisiteRemoved> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "prerequisite-removed", includePrerequisiteLookup: false, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.epic.issue-linked</c> and triggers
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> on the epic that
/// linked the issue. This is the durable convergence path for link
/// operations: <c>LinkIssueAsync</c> commits the membership row then
/// calls recompute inline, but a crash between commit and recompute
/// would leave a linked-but-unadvanced epic. The event is durable
/// (persisted to the event store before the grain returns), so the
/// dispatcher redelivers it until the handler succeeds — closing the
/// gap left by the removed poll-driven sweep.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.EpicIssueLinked)]
public sealed class EpicIssueLinkedHandler : ICloudEventHandler<Epic.Domain.Events.EpicIssueLinked>
{
    private readonly EpicEventRecomputeDispatcher _dispatcher;
    private readonly EpicIssueAffiliationDispatcher _affiliationDispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicIssueLinkedHandler(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<EpicIssueLinkedHandler> log)
    {
        _dispatcher = new EpicEventRecomputeDispatcher(grains, log);
        _affiliationDispatcher = new EpicIssueAffiliationDispatcher(grains, dbFactory);
    }

    public bool Filter(CloudEvent<Epic.Domain.Events.EpicIssueLinked> evt) => true;

    public async Task HandleAsync(CloudEvent<Epic.Domain.Events.EpicIssueLinked> evt, CancellationToken ct)
    {
        await _affiliationDispatcher.DispatchAsync(evt.Id, evt.Extensions, evt.Data.IssueId, ct).ConfigureAwait(false);
        await _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "issue-linked", ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Subscribes to <c>com.mohist.epic.issue-unlinked</c> and recomputes the
/// owning epic after membership removal. The unlink command performs the
/// same recompute inline; this durable path converges after a crash between
/// the committed unlink and that inline call.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.EpicIssueUnlinked)]
public sealed class EpicIssueUnlinkedHandler : ICloudEventHandler<Epic.Domain.Events.EpicIssueUnlinked>
{
    private readonly EpicEventRecomputeDispatcher _dispatcher;
    private readonly EpicIssueAffiliationDispatcher _affiliationDispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicIssueUnlinkedHandler(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory,
        ILogger<EpicIssueUnlinkedHandler> log)
    {
        _dispatcher = new EpicEventRecomputeDispatcher(grains, log);
        _affiliationDispatcher = new EpicIssueAffiliationDispatcher(grains, dbFactory);
    }

    public bool Filter(CloudEvent<Epic.Domain.Events.EpicIssueUnlinked> evt) => true;

    public async Task HandleAsync(CloudEvent<Epic.Domain.Events.EpicIssueUnlinked> evt, CancellationToken ct)
    {
        await _affiliationDispatcher.DispatchAsync(evt.Id, evt.Extensions, evt.Data.IssueId, ct).ConfigureAwait(false);
        await _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "issue-unlinked", ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Subscribes to <c>com.mohist.epic.status-changed</c> transitions into
/// <c>running</c> and re-drives <see cref="IEpicGrain.RecomputeProgressAsync"/>.
/// This is the durable recovery intent for a command-path start: if a crash
/// occurs after the running transition commits but before
/// <c>TryStartNextAsync</c> advances the first issue, this handler re-drives
/// the recompute on redelivery. It also covers the auto-mark-done path for
/// an epic that starts with no open members.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.EpicStatusChanged)]
public sealed class EpicRunningStatusHandler : ICloudEventHandler<Epic.Domain.Events.EpicStatusChanged>
{
    private readonly EpicEventRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicRunningStatusHandler(
        IGrainFactory grains,
        ILogger<EpicRunningStatusHandler> log)
    {
        _dispatcher = new EpicEventRecomputeDispatcher(grains, log);
    }

    public bool Filter(CloudEvent<Epic.Domain.Events.EpicStatusChanged> evt) =>
        string.Equals(evt.Data.NewStatus, "running", StringComparison.Ordinal);

    public Task HandleAsync(CloudEvent<Epic.Domain.Events.EpicStatusChanged> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "status-changed", ct);
}

[Subscription(Type = EventCatalog.ReverseDns.EpicStatusChanged)]
public sealed class EpicAffiliationStatusChangedHandler : ICloudEventHandler<Epic.Domain.Events.EpicStatusChanged>
{
    private readonly EpicIssueAffiliationDispatcher _dispatcher;

    public EpicAffiliationStatusChangedHandler(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        _dispatcher = new EpicIssueAffiliationDispatcher(grains, dbFactory);
    }

    public bool Filter(CloudEvent<Epic.Domain.Events.EpicStatusChanged> evt) => true;

    public Task HandleAsync(CloudEvent<Epic.Domain.Events.EpicStatusChanged> evt, CancellationToken ct) =>
        _dispatcher.DispatchEpicAsync(evt.Id, evt.Extensions, ct);
}

/// <summary>
/// Subscribes to <c>com.mohist.epic.start-attempt-failed</c> and re-drives
/// <see cref="IEpicGrain.RecomputeProgressAsync"/> on the epic. When
/// <c>TryStartNextAsync</c> catches a transient <c>StartWorkAsync</c> failure
/// under <c>PreserveRunning</c>, the epic records this event; the durable
/// dispatcher redelivers it with backoff until the recompute succeeds —
/// recovering a running-but-idle epic that would otherwise stay stuck. The
/// recompute uses <c>Propagate</c> start-failure mode inside the grain, so a
/// permanently failing start surfaces to the dispatcher for dead-lettering
/// rather than being silently swallowed again.
/// </summary>
[Subscription(Type = EventCatalog.ReverseDns.EpicStartAttemptFailed)]
public sealed class EpicStartRetryHandler : ICloudEventHandler<Epic.Domain.Events.EpicStartAttemptFailed>
{
    private readonly EpicEventRecomputeDispatcher _dispatcher;

    [ActivatorUtilitiesConstructor]
    public EpicStartRetryHandler(
        IGrainFactory grains,
        ILogger<EpicStartRetryHandler> log)
    {
        _dispatcher = new EpicEventRecomputeDispatcher(grains, log);
    }

    public bool Filter(CloudEvent<Epic.Domain.Events.EpicStartAttemptFailed> evt) => true;

    public Task HandleAsync(CloudEvent<Epic.Domain.Events.EpicStartAttemptFailed> evt, CancellationToken ct) =>
        _dispatcher.DispatchAsync(evt.Id, evt.Extensions, evtType: "start-attempt-failed", ct);
}

/// <summary>
/// Shared dispatch logic for issue-event → EpicGrain recompute-progress
/// wiring. <see cref="EpicAutoDoneHandler"/> (completed),
/// <see cref="EpicCancelledHandler"/> (cancelled), and
/// <see cref="EpicDraftChangedHandler"/> (undraft) funnel here so the
/// CloudEvent <c>projectid</c>/<c>issueid</c>/<c>issue</c> extension
/// parsing, epic lookup, and grain dispatch stay in one place. The
/// issue number is read from the unified <c>issue</c> key with a legacy
/// <c>issueno</c> fallback for pre-change historical rows that were
/// never backfilled (Non-Goal forbids rewriting history). When
/// <paramref name="includePrerequisiteLookup"/> is set, also reverse-looks-up
/// epics whose members depend on the event's issue as an external
/// prerequisite — the owning-epic lookup misses those because the
/// prerequisite has no direct active membership. Kept package-internal.
/// </summary>
internal sealed class EpicProgressRecomputeDispatcher
{
    private readonly IServiceScopeFactory? _scopes;
    private readonly EpicQuerier? _epicQuerier;
    private readonly IGrainFactory _grains;
    private readonly ILogger _log;

    public EpicProgressRecomputeDispatcher(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public EpicProgressRecomputeDispatcher(
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
        bool includePrerequisiteLookup,
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

        var issueNumber = TryReadIssueNumber(extensions);
        var epicIds = new HashSet<string>(StringComparer.Ordinal);

        if (_epicQuerier is not null)
        {
            var direct = await _epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
            if (direct is not null) epicIds.Add(direct);
            if (includePrerequisiteLookup && issueNumber is int n)
            {
                var dependent = await _epicQuerier
                    .GetEpicIdsDependentOnPrerequisiteAsync(projectId, n)
                    .ConfigureAwait(false);
                foreach (var id in dependent) epicIds.Add(id);
            }
        }
        else
        {
            await using var scope = _scopes!.CreateAsyncScope();
            var epicQuerier = scope.ServiceProvider.GetRequiredService<EpicQuerier>();
            var direct = await epicQuerier.GetEpicIdForIssueAsync(projectId, issueId).ConfigureAwait(false);
            if (direct is not null) epicIds.Add(direct);
            if (includePrerequisiteLookup && issueNumber is int n)
            {
                var dependent = await epicQuerier
                    .GetEpicIdsDependentOnPrerequisiteAsync(projectId, n)
                    .ConfigureAwait(false);
                foreach (var id in dependent) epicIds.Add(id);
            }
        }

        foreach (var epicId in epicIds)
        {
            var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epicId}");
            await grain.RecomputeProgressAsync().ConfigureAwait(false);
        }
    }

    internal static int? TryReadIssueNumber(IReadOnlyDictionary<string, string> extensions)
    {
        var text = TryReadIssueNumberText(extensions);
        return int.TryParse(text, out var n) ? n : null;
    }

    private static string? TryReadIssueNumberText(IReadOnlyDictionary<string, string> extensions)
    {
        if (extensions.TryGetValue(EventCatalog.Lineage.Issue, out var unifiedText)
            && !string.IsNullOrWhiteSpace(unifiedText))
        {
            return unifiedText;
        }

        if (extensions.TryGetValue("issueno", out var legacyText)
            && !string.IsNullOrWhiteSpace(legacyText))
        {
            return legacyText;
        }

        return null;
    }
}

/// <summary>
/// Dispatch logic for epic-event → EpicGrain recompute-progress wiring.
/// Epic events carry <c>projectid</c> + <c>epicid</c> on the envelope
/// (stamped by <c>EpicGrain.PersistEpicEventsAsync</c>), so no reverse
/// lookup is needed — the epic identity is already known. Used by
/// <see cref="EpicIssueLinkedHandler"/> and the start-retry handler.
/// </summary>
internal sealed class EpicEventRecomputeDispatcher
{
    private readonly IGrainFactory _grains;
    private readonly ILogger _log;

    public EpicEventRecomputeDispatcher(IGrainFactory grains, ILogger log)
    {
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
        if (!extensions.TryGetValue("epicid", out var epicId) || string.IsNullOrWhiteSpace(epicId))
        {
            _log.LogDebug(
                "{EvtType} event missing epicid extension; skipping (event {EventId})",
                evtType, eventId);
            return;
        }

        var grain = _grains.GetGrain<IEpicGrain>($"{projectId}:{epicId}");
        await grain.RecomputeProgressAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Dispatch logic for epic-link/unlink → IssueGrain affiliation write
/// (D5 denormalization). The synchronous push inside <c>EpicGrain</c>
/// link/unlink is best-effort; this durable path re-applies the same
/// denormalization on every redelivery so drift between the join row
/// (<c>EpicIssueRow</c>) and the issue's denormalized <c>EpicId</c> is
/// bounded and self-healing.
/// </summary>
internal sealed class EpicIssueAffiliationDispatcher
{
    private readonly IGrainFactory _grains;
    private readonly IDbContextFactory<MohistDbContext> _dbFactory;
    public EpicIssueAffiliationDispatcher(
        IGrainFactory grains,
        IDbContextFactory<MohistDbContext> dbFactory)
    {
        _grains = grains;
        _dbFactory = dbFactory;
    }

    public async Task DispatchAsync(
        string eventId,
        IReadOnlyDictionary<string, string> extensions,
        string issueId,
        CancellationToken ct)
    {
        if (!extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(issueId))
        {
            throw new InvalidOperationException(
                $"Affiliation event '{eventId}' is missing its issue id payload.");
        }

        string? epicId;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            epicId = await EpicIssueAffiliationResolver.ResolveAsync(db, projectId, issueId, ct: ct).ConfigureAwait(false);
        }
        var grain = _grains.GetGrain<Mohist.Server.Issue.Grains.IIssueGrain>(
            Mohist.Server.Infrastructure.Orleans.GrainKey.Issue(issueId));
        await grain.SetEpicAffiliationAsync(epicId).ConfigureAwait(false);
    }

    public async Task DispatchEpicAsync(
        string eventId,
        IReadOnlyDictionary<string, string> extensions,
        CancellationToken ct)
    {
        if (!extensions.TryGetValue(EventCatalog.Lineage.ProjectId, out var projectId)
            || string.IsNullOrWhiteSpace(projectId)
            || !extensions.TryGetValue(EventCatalog.Lineage.EpicId, out var epicId)
            || string.IsNullOrWhiteSpace(epicId))
        {
            throw new InvalidOperationException(
                $"Epic affiliation event '{eventId}' is missing projectid or epicid.");
        }

        IReadOnlyList<string> issueIds;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            issueIds = await db.EpicIssues.AsNoTracking()
                .Where(link => link.ProjectId == projectId && link.EpicId == epicId)
                .OrderBy(link => link.IssueId)
                .Select(link => link.IssueId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        foreach (var issueId in issueIds)
            await DispatchAsync(eventId, extensions, issueId, ct).ConfigureAwait(false);
    }
}
