using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Workflow.Domain.Run;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Events.Subscriptions;

/// <summary>
/// Server-side projection that turns the four authoritative "operator
/// signal" CloudEvents into project-scoped inbox items:
/// <list type="bullet">
///   <item><c>com.mohist.workflow.run.failed</c>             → <c>workflow_failed</c></item>
///   <item><c>com.mohist.workflow.stage.approval-requested</c> → <c>approval_requested</c></item>
///   <item><c>com.mohist.issue.work-started</c>               → <c>issue_started</c></item>
///   <item><c>com.mohist.issue.work-completed</c>             → <c>issue_completed</c></item>
/// </list>
/// The handler is a single <see cref="ICloudEventHandler"/> over a
/// pipe-separated <see cref="SubscriptionAttribute"/> because the four
/// payload types (<see cref="WorkflowRunFailed"/>,
/// <see cref="StageApprovalRequested"/>, <see cref="IssueWorkStarted"/>,
/// <see cref="IssueWorkCompleted"/>) have disjoint shapes and we only
/// need a small number of fields from each. Branches dispatch through
/// <see cref="WorkflowStageLockReleaseHandler.ExtractWorkflowRunId"/>
/// for workflow events, or read the issue-event
/// <c>projectid</c>/<c>issueid</c>/<c>issueno</c> extensions
/// stamped by <c>IssueGrain</c>.
///
/// Identity resolution starts from event metadata, then validates it
/// against the loaded issue before writing an inbox item:
/// <list type="bullet">
///   <item>Workflow events → <see cref="IWorkflowRunStore.LoadAsync"/> reads
///         <see cref="WorkflowRunMetadata.Annotations"/> for
///         <c>projectId</c>/<c>issueId</c>/<c>issueNumber</c> — the same
///         source <c>WorkflowGrain.GetProjectId</c> uses.</item>
///   <item>Issue events → extensions identify the candidate issue; the
///         loaded issue is the source of truth for project and number.</item>
/// </list>
///
/// Idempotency is delegated to <see cref="InboxStore.InsertAsync"/>:
/// the source plus event id index dedupes replays at the store level,
/// so this handler does not need its own dedup bookkeeping.
///
/// Exceptions are logged and swallowed: the bus already tolerates
/// handler failures (see <see cref="InMemoryEventBus"/>), and the spec
/// states the projection must never block workflow / issue execution.
///
/// <para>
/// <b>Realtime hint</b>. On a successful, non-duplicate
/// <see cref="InboxStore.InsertAsync"/> the handler publishes exactly one
/// <c>com.mohist.inbox.item-persisted</c> CloudEvent carrying an
/// <see cref="InboxItemPersistedHint"/> identity payload and an
/// <c>extensions["projectid"]</c> stamp so the dispatcher can route it
/// project-scoped. The publish is awaited inline <i>after</i> the insert
/// returns and inherits <see cref="HandleAsync"/>'s swallow-and-log
/// guard, so a publish failure cannot break the source-event projection.
/// </para>
/// </summary>
[Subscription(Type =
    "com.mohist.workflow.run.failed|" +
    "com.mohist.workflow.stage.approval-requested|" +
    "com.mohist.issue.work-started|" +
    "com.mohist.issue.work-completed")]
public sealed class InboxProjectionHandler : ICloudEventHandler
{
    private const string HintSource = "/mohist/inbox";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboxProjectionHandler> _log;

    public InboxProjectionHandler(
        IServiceScopeFactory scopeFactory,
        ILogger<InboxProjectionHandler> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null && TryResolve(evt.Type, out _);

    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        try
        {
            await ProjectAsync(evt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The bus already swallows handler exceptions by design; this
            // catch is the handler-side mirror of the same defense — we
            // log and never propagate. The source-of-truth events are
            // durable in the event store; a future event-store replay /
            // backfill keyed on SourceEventId would re-create the missed
            // item without duplicates.
            _log.LogWarning(ex,
                "Inbox projection handler failed for event {EventType} {EventId}",
                evt.Type, evt.Id);
        }
    }

    private async Task ProjectAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!TryResolve(evt.Type, out var kind))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<InboxStore>();
        var issueStore = scope.ServiceProvider.GetRequiredService<IStateStore<DomainIssue>>();

        var resolved = evt.Type switch
        {
            EventCatalog.ReverseDns.WorkflowRunFailed =>
                await ResolveFromWorkflowRunAsync(evt, scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>(), ct).ConfigureAwait(false),
            EventCatalog.ReverseDns.StageApprovalRequested =>
                await ResolveFromWorkflowRunAsync(evt, scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>(), ct).ConfigureAwait(false),
            EventCatalog.ReverseDns.IssueWorkStarted =>
                ResolveFromIssueExtensions(evt),
            EventCatalog.ReverseDns.IssueWorkCompleted =>
                ResolveFromIssueExtensions(evt),
            _ => null,
        };

        if (resolved is null)
            return;

        var issue = await ResolveIssueAsync(resolved.Value, issueStore).ConfigureAwait(false);
        if (issue is null)
        {
            return;
        }

        var subscriptionStore = scope.ServiceProvider.GetRequiredService<InboxSubscriptionStore>();
        var subscription = await subscriptionStore.GetAsync(resolved.Value.ProjectId, ct).ConfigureAwait(false);
        if (!subscription.IsEnabled(kind))
        {
            return;
        }

        var draft = new InboxItemDraft(
            ProjectId: resolved.Value.ProjectId,
            IssueId: resolved.Value.IssueId,
            IssueNumber: resolved.Value.IssueNumber,
            IssueTitle: issue.Title,
            NotificationKind: kind,
            SourceEventSource: evt.Source.ToString(),
            SourceEventId: evt.Id);

        var result = await inboxStore.InsertAsync(draft, ct).ConfigureAwait(false);
        if (result.AlreadyExisted)
        {
            return;
        }

        var hint = new InboxItemPersistedHint(
            ItemId: result.Id,
            ProjectId: draft.ProjectId,
            Kind: kind,
            IssueId: draft.IssueId,
            IssueNumber: draft.IssueNumber);

        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projectid"] = draft.ProjectId,
        };

        // The hint publish reuses the same async scope that resolved
        // InboxStore. We cannot inject IEventPublisher through the
        // handler constructor: the bus wires InboxProjectionHandler as a
        // singleton, and IEventPublisher resolves to the InMemoryEventBus
        // singleton whose constructor enumerates all handler subscriptions,
        // which would close a DI cycle on first construction. Resolving
        // the publisher from the already-open scope avoids this and is
        // semantically identical — IEventPublisher is itself a singleton,
        // so every request would land on the same instance.
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        await eventPublisher
            .PublishAsync(hint, EventCatalog.ReverseDns.InboxItemPersisted, HintSource, extensions: extensions, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task<ResolvedIdentity?> ResolveFromWorkflowRunAsync(CloudEvent evt, IWorkflowRunStore workflowRunStore, CancellationToken ct)
    {
        var workflowRunId = WorkflowStageLockReleaseHandler.ExtractWorkflowRunId(evt.Source.ToString());
        if (string.IsNullOrEmpty(workflowRunId))
        {
            _log.LogDebug(
                "Inbox projection skipped: workflow event {EventId} source {Source} has no workflow run id",
                evt.Id, evt.Source);
            return null;
        }

        var run = await workflowRunStore.LoadAsync(workflowRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            _log.LogDebug(
                "Inbox projection skipped: workflow run {WorkflowRunId} for event {EventId} not found",
                workflowRunId, evt.Id);
            return null;
        }

        var annotations = run.Metadata?.Annotations;
        if (annotations is null
            || !annotations.TryGetValue("projectId", out var projectId) || string.IsNullOrWhiteSpace(projectId)
            || !annotations.TryGetValue("issueId", out var issueId) || string.IsNullOrWhiteSpace(issueId)
            || !annotations.TryGetValue("issueNumber", out var issueNumberText) || string.IsNullOrWhiteSpace(issueNumberText)
            || !int.TryParse(issueNumberText, out var issueNumber))
        {
            _log.LogDebug(
                "Inbox projection skipped: workflow run {WorkflowRunId} for event {EventId} has no projectId/issueId/issueNumber annotations",
                workflowRunId, evt.Id);
            return null;
        }

        return new ResolvedIdentity(projectId, issueId, issueNumber);
    }

    private static ResolvedIdentity? ResolveFromIssueExtensions(CloudEvent evt)
    {
        var extensions = evt.Extensions;
        if (!extensions.TryGetValue("projectid", out var projectId) || string.IsNullOrWhiteSpace(projectId)
            || !extensions.TryGetValue("issueid", out var issueId) || string.IsNullOrWhiteSpace(issueId)
            || !extensions.TryGetValue("issueno", out var issueNumberText) || string.IsNullOrWhiteSpace(issueNumberText)
            || !int.TryParse(issueNumberText, out var issueNumber))
        {
            return null;
        }

        return new ResolvedIdentity(projectId, issueId, issueNumber);
    }

    private async Task<DomainIssue?> ResolveIssueAsync(ResolvedIdentity resolved, IStateStore<DomainIssue> issueStore)
    {
        try
        {
            var issue = await issueStore.LoadAsync(resolved.IssueId).ConfigureAwait(false);
            if (issue is null)
                return null;

            if (!string.Equals(issue.ProjectId, resolved.ProjectId, StringComparison.Ordinal)
                || issue.Number != resolved.IssueNumber)
            {
                _log.LogDebug(
                    "Inbox projection skipped: event identity project {EventProjectId} issue {IssueId} number {EventIssueNumber} disagrees with loaded issue project {IssueProjectId} number {IssueNumber}",
                    resolved.ProjectId,
                    resolved.IssueId,
                    resolved.IssueNumber,
                    issue.ProjectId,
                    issue.Number);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(issue.Title))
                return issue;

            _log.LogDebug(
                "Inbox projection skipped: issue {IssueId} has no title snapshot",
                resolved.IssueId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Inbox projection: failed to load issue {IssueId} for title snapshot",
                resolved.IssueId);
        }
        return null;
    }

    private static bool TryResolve(string? type, out string kind)
    {
        switch (type)
        {
            case EventCatalog.ReverseDns.WorkflowRunFailed:
                kind = NotificationKinds.WorkflowFailed;
                return true;
            case EventCatalog.ReverseDns.StageApprovalRequested:
                kind = NotificationKinds.ApprovalRequested;
                return true;
            case EventCatalog.ReverseDns.IssueWorkStarted:
                kind = NotificationKinds.IssueStarted;
                return true;
            case EventCatalog.ReverseDns.IssueWorkCompleted:
                kind = NotificationKinds.IssueCompleted;
                return true;
            default:
                kind = string.Empty;
                return false;
        }
    }

    private readonly record struct ResolvedIdentity(string ProjectId, string IssueId, int IssueNumber);
}
