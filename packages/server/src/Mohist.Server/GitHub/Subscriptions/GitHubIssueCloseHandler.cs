using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Translates a GitHub <c>closed</c> event according to the linked Issue's
/// execution ownership. No-Workflow Issues use GitHub's <c>state_reason</c>:
/// <c>completed</c> becomes Done and <c>not_planned</c> becomes Cancelled.
/// Workflow Issues can be withdrawn only before the Integrate stage; the
/// Integrate boundary and later states are delivery echoes, including the
/// automatic close caused by merging a Pull Request.
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubIssuesClosed,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueCloseHandler")]
public sealed class GitHubIssueCloseHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueCloseHandler> _log;

    public GitHubIssueCloseHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubIssueCloseHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.GitHubIssuesClosed, StringComparison.Ordinal)
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => CloseAsync(evt, ct);

    private async Task CloseAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubIssueEventPayload.Parse(evt.Data);
        if (payload is null)
        {
            _log.LogDebug("GitHub close skipped: event {EventId} carries no readable issue payload", evt.Id);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;

        var link = await sp.GetRequiredService<GitHubIssueLinkStore>()
            .GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        if (link is null)
            return;

        var issueStore = sp.GetRequiredService<IIssueStore>();
        var issue = await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        if (issue is null || issue.Status is IssueStatus.Done or IssueStatus.Cancelled)
            return;

        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        try
        {
            if (issue.NoWorkflow)
            {
                switch (payload.StateReason)
                {
                    case "completed":
                        await grain.MarkDoneAsync();
                        break;
                    case "not_planned":
                        await grain.CancelAsync();
                        break;
                    default:
                        _log.LogWarning(
                            "GitHub close ignored for no-workflow issue #{IssueNumber}: unsupported state_reason {StateReason}",
                            link.IssueNumber,
                            payload.StateReason ?? "<missing>");
                        break;
                }
                return;
            }

            if (issue.WorkflowRunId is { } workflowRunId)
            {
                WorkflowWithdrawalResult withdrawal;
                try
                {
                    withdrawal = await _grains.GetGrain<IWorkflowGrain>(workflowRunId)
                        .WithdrawIfBeforeIntegrateAsync("github-close");
                }
                catch (InvalidOperationException ex)
                {
                    _log.LogDebug(
                        "GitHub close workflow withdrawal no-op for issue #{IssueNumber}: {Message}",
                        link.IssueNumber, ex.Message);
                    return;
                }

                if (!withdrawal.IsApplied)
                    return;
            }

            await grain.CancelAsync();
        }
        catch (InvalidOperationException ex)
        {
            _log.LogDebug(
                "GitHub close no-op: issue #{IssueNumber} cannot transition ({Message})",
                link.IssueNumber, ex.Message);
        }
    }
}
