using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.Infrastructure.Data;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Inbox;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Notifications;
using Mohist.Server.Workflow.Domain.Run;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.Events.Subscriptions;

[Subscription(Type =
    EventCatalog.ReverseDns.WorkflowRunFailed + "|" +
    EventCatalog.ReverseDns.StageApprovalRequested + "|" +
    EventCatalog.ReverseDns.IssueWorkStarted + "|" +
    EventCatalog.ReverseDns.IssueCompleted)]
public sealed class HermesIssueNotificationHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<HermesNotificationOptions> _options;
    private readonly HermesIssueNotificationRenderer _renderer;
    private readonly IHermesWebhookClient _client;
    private readonly IHermesIssueNotificationDispatcher _dispatcher;
    private readonly ILogger<HermesIssueNotificationHandler> _log;

    public HermesIssueNotificationHandler(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<HermesNotificationOptions> options,
        HermesIssueNotificationRenderer renderer,
        IHermesWebhookClient client,
        IHermesIssueNotificationDispatcher dispatcher,
        ILogger<HermesIssueNotificationHandler> log)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _renderer = renderer;
        _client = client;
        _dispatcher = dispatcher;
        _log = log;
    }

    public bool Filter(CloudEvent evt) => evt is not null && TryResolveNotificationType(evt.Type, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.IsWebhookConfigured || !TryResolveNotificationType(evt.Type, out var notificationType))
            return Task.CompletedTask;

        if (!options.IsEnabled(notificationType))
            return Task.CompletedTask;

        ct.ThrowIfCancellationRequested();
        _dispatcher.Dispatch(backgroundCt => DeliverAsync(evt, notificationType, backgroundCt));
        return Task.CompletedTask;
    }

    private async Task DeliverAsync(CloudEvent evt, string notificationType, CancellationToken ct)
    {
        try
        {
            var draft = await BuildDraftAsync(evt, notificationType, ct).ConfigureAwait(false);
            if (draft is null)
                return;

            await _client.SendAsync(_renderer.Render(draft), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogDebug(
                "Hermes issue notification delivery canceled for event {EventType} {EventId}",
                evt.Type,
                evt.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Hermes issue notification delivery failed for event {EventType} {EventId}",
                evt.Type,
                evt.Id);
        }
    }

    private async Task<HermesIssueNotificationDraft?> BuildDraftAsync(
        CloudEvent evt,
        string notificationType,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var issueStore = scope.ServiceProvider.GetRequiredService<IStateStore<DomainIssue>>();

        var resolved = evt.Type switch
        {
            EventCatalog.ReverseDns.WorkflowRunFailed =>
                await ResolveFromWorkflowRunAsync(evt, scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>(), ct).ConfigureAwait(false),
            EventCatalog.ReverseDns.StageApprovalRequested =>
                await ResolveFromWorkflowRunAsync(evt, scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>(), ct).ConfigureAwait(false),
            EventCatalog.ReverseDns.IssueWorkStarted => ResolveFromIssueExtensions(evt),
            EventCatalog.ReverseDns.IssueCompleted => ResolveFromIssueExtensions(evt),
            _ => null,
        };

        if (resolved is null)
            return null;

        var issue = await ResolveIssueAsync(resolved.Value, issueStore).ConfigureAwait(false);
        if (issue is null)
            return null;

        var stage = evt.Type == EventCatalog.ReverseDns.StageApprovalRequested
            ? DeserializeData<StageApprovalRequested>(evt)?.Stage
            : null;
        var failureReason = evt.Type == EventCatalog.ReverseDns.WorkflowRunFailed
            ? DeserializeData<WorkflowRunFailed>(evt)?.Message
            : null;

        return new HermesIssueNotificationDraft(
            notificationType,
            evt.Type,
            evt.Id,
            evt.Time,
            resolved.Value.ProjectId,
            resolved.Value.IssueId,
            resolved.Value.IssueNumber,
            issue.Title,
            resolved.Value.WorkflowRunId,
            stage,
            failureReason);
    }

    private async Task<ResolvedIdentity?> ResolveFromWorkflowRunAsync(
        CloudEvent evt,
        IWorkflowRunStore workflowRunStore,
        CancellationToken ct)
    {
        var workflowRunId = WorkflowStageLockReleaseHandler.ExtractWorkflowRunId(evt.Source.ToString());
        if (string.IsNullOrEmpty(workflowRunId))
            return null;

        var run = await workflowRunStore.LoadAsync(workflowRunId, ct).ConfigureAwait(false);
        var annotations = run?.Metadata.Annotations;
        if (annotations is null
            || !annotations.TryGetValue("projectId", out var projectId) || string.IsNullOrWhiteSpace(projectId)
            || !annotations.TryGetValue("issueId", out var issueId) || string.IsNullOrWhiteSpace(issueId)
            || !annotations.TryGetValue("issueNumber", out var issueNumberText) || string.IsNullOrWhiteSpace(issueNumberText)
            || !int.TryParse(issueNumberText, out var issueNumber))
        {
            return null;
        }

        return new ResolvedIdentity(projectId, issueId, issueNumber, workflowRunId);
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

        var workflowRunId = evt.Type switch
        {
            EventCatalog.ReverseDns.IssueWorkStarted => DeserializeData<IssueWorkStarted>(evt)?.WorkflowRunId,
            EventCatalog.ReverseDns.IssueCompleted => DeserializeData<IssueCompleted>(evt)?.WorkflowRunId,
            _ => null,
        };

        return new ResolvedIdentity(projectId, issueId, issueNumber, workflowRunId);
    }

    private async Task<DomainIssue?> ResolveIssueAsync(ResolvedIdentity resolved, IStateStore<DomainIssue> issueStore)
    {
        var issue = await issueStore.LoadAsync(resolved.IssueId).ConfigureAwait(false);
        if (issue is null)
            return null;

        if (!string.Equals(issue.ProjectId, resolved.ProjectId, StringComparison.Ordinal)
            || issue.Number != resolved.IssueNumber
            || string.IsNullOrWhiteSpace(issue.Title))
        {
            return null;
        }

        return issue;
    }

    private static T? DeserializeData<T>(CloudEvent evt) where T : class =>
        evt.Data is { } data ? data.Deserialize<T>(CloudEvent.JsonOptions) : null;

    private static bool TryResolveNotificationType(string? type, out string notificationType)
    {
        switch (type)
        {
            case EventCatalog.ReverseDns.WorkflowRunFailed:
                notificationType = NotificationKinds.WorkflowFailed;
                return true;
            case EventCatalog.ReverseDns.StageApprovalRequested:
                notificationType = NotificationKinds.ApprovalRequested;
                return true;
            case EventCatalog.ReverseDns.IssueWorkStarted:
                notificationType = NotificationKinds.IssueStarted;
                return true;
            case EventCatalog.ReverseDns.IssueCompleted:
                notificationType = NotificationKinds.IssueCompleted;
                return true;
            default:
                notificationType = string.Empty;
                return false;
        }
    }

    private readonly record struct ResolvedIdentity(
        string ProjectId,
        string IssueId,
        int IssueNumber,
        string? WorkflowRunId);
}
