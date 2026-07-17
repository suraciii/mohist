using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Inbox;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
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
///   <item><c>com.mohist.issue.completed</c>                  → <c>issue_completed</c></item>
/// </list>
/// The handler is a single <see cref="ICloudEventHandler"/> over a
/// pipe-separated <see cref="SubscriptionAttribute"/> because the four
/// payload types (<see cref="WorkflowRunFailed"/>,
/// <see cref="StageApprovalRequested"/>, <see cref="IssueWorkStarted"/>,
/// <see cref="IssueCompleted"/>) have disjoint shapes and we only
/// need a small number of fields from each. Branches dispatch through
/// canonical envelope extensions
/// for workflow events, or read the issue-event
/// <c>projectid</c>/<c>issue</c> extensions
/// stamped by <c>IssueStore</c> when it appends the event row.
///
/// Identity resolution starts from the canonical envelope extensions, then
/// validates it against the loaded issue before writing an inbox item. The
/// payload and source URI are never used to select the target issue.
///
/// Idempotency is delegated to <see cref="InboxStore.InsertAsync"/>:
/// the source plus event id index dedupes replays at the store level,
/// so this handler does not need its own dedup bookkeeping.
///
/// Required projection and hint-publish failures propagate to the durable
/// dispatcher so its retry and dead-letter policy can observe them. Missing,
/// disabled, or mismatched domain data remains an intentional no-op.
///
/// <para>
/// <b>Realtime hint</b>. The inbox projection and its
/// <c>com.mohist.inbox.item-persisted</c> CloudEvent carrying an
/// <see cref="InboxItemPersistedHint"/> identity payload and an
/// <c>extensions["projectid"]</c>/<c>["issue"]</c>
/// stamp lifted from the <see cref="InboxItemDraft"/> already held in
/// scope (no additional lookup) so the dispatcher can route it
/// project-scoped commit in one transaction. A failed event append rolls the
/// projection back so dispatcher retry can complete both writes exactly once.
/// </para>
/// </summary>
[Subscription(Type =
    "com.mohist.workflow.run.failed|" +
    "com.mohist.workflow.stage.approval-requested|" +
    "com.mohist.issue.work-started|" +
    EventCatalog.ReverseDns.IssueCompleted)]
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

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => ProjectAsync(evt, ct);

    private async Task ProjectAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!TryResolve(evt.Type, out var kind))
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<InboxStore>();
        var issueStore = scope.ServiceProvider.GetRequiredService<IStateStore<DomainIssue>>();

        var resolved = evt.Type switch
        {
            EventCatalog.ReverseDns.WorkflowRunFailed => ResolveFromEnvelope(evt),
            EventCatalog.ReverseDns.StageApprovalRequested => ResolveFromEnvelope(evt),
            EventCatalog.ReverseDns.IssueWorkStarted => ResolveFromEnvelope(evt),
            EventCatalog.ReverseDns.IssueCompleted => ResolveFromEnvelope(evt),
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
            IssueNumber: resolved.Value.IssueNumber,
            IssueTitle: issue.Title,
            NotificationKind: kind,
            SourceEventSource: evt.Source.ToString(),
            SourceEventId: evt.Id);

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MohistDbContext>>();
        var eventStore = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var time = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var result = await inboxStore.InsertAsync(db, draft, ct).ConfigureAwait(false);
        if (result.AlreadyExisted)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        var hint = new InboxItemPersistedHint(
            ItemId: result.Id,
            ProjectId: draft.ProjectId,
            Kind: kind,
            IssueNumber: draft.IssueNumber);

        var extensions = InboxLineage.BuildExtensions(draft, evt.Extensions);

        var envelope = new CloudEvent(
            id: Guid.NewGuid().ToString(),
            source: new Uri(HintSource, UriKind.Relative),
            type: EventCatalog.ReverseDns.InboxItemPersisted,
            time: time.GetUtcNow(),
            data: JsonSerializer.SerializeToElement(hint, CloudEvent.JsonOptions),
            extensions: extensions);
        await eventStore.AppendAsync(db, envelope, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static ResolvedIdentity? ResolveFromEnvelope(CloudEvent evt)
    {
        return CloudEventLineage.TryReadIssueContext(evt, out var context)
            ? new ResolvedIdentity(context.ProjectId, context.IssueNumber)
            : null;
    }

    private async Task<DomainIssue?> ResolveIssueAsync(ResolvedIdentity resolved, IStateStore<DomainIssue> issueStore)
    {
        var issue = await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(resolved.ProjectId, resolved.IssueNumber))).ConfigureAwait(false);
        if (issue is null)
            return null;

        if (!string.Equals(issue.ProjectId, resolved.ProjectId, StringComparison.Ordinal)
            || issue.Number != resolved.IssueNumber)
        {
            _log.LogDebug(
                "Inbox projection skipped: event identity project {EventProjectId} number {EventIssueNumber} disagrees with loaded issue project {IssueProjectId} number {IssueNumber}",
                resolved.ProjectId,
                resolved.IssueNumber,
                issue.ProjectId,
                issue.Number);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(issue.Title))
            return issue;

        _log.LogDebug(
            "Inbox projection skipped: issue #{IssueNumber} has no title snapshot",
            resolved.IssueNumber);
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
            case EventCatalog.ReverseDns.IssueCompleted:
                kind = NotificationKinds.IssueCompleted;
                return true;
            default:
                kind = string.Empty;
                return false;
        }
    }

    private readonly record struct ResolvedIdentity(string ProjectId, int IssueNumber);
}
