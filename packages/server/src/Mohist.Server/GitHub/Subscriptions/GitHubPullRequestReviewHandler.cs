using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Approval translator: a submitted PR review by an approver resolves the
/// Check gate of the issue whose branch the PR head carries. The branch
/// names the Mohist issue (<c>mo/issue-N</c>); a branch that does not parse
/// is ignored, as are reviewers outside the connection's approver list
/// (an empty list disables the capability entirely). Only a review arriving
/// while the issue's active run is actually awaiting approval at the Check
/// stage counts — the decision is taken from the event as delivered, never
/// re-derived from later dismissals or stale reviews.
/// <para>
/// <c>approved</c> approves the gate; <c>changes_requested</c> sends the
/// stage back with the review body as the reason; <c>commented</c> is a
/// no-op. Every decision is attributed as <c>github:&lt;login&gt;</c>.
/// The workflow grain refuses to act once the stage is no longer awaiting
/// approval, so duplicate deliveries (GitHub is at-least-once) and reviews
/// overtaken by state changes land in a no-op, keeping the handler
/// idempotent.
/// </para>
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubPullRequestReviewed,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubPullRequestReviewHandler")]
public sealed class GitHubPullRequestReviewHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubPullRequestReviewHandler> _log;

    public GitHubPullRequestReviewHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubPullRequestReviewHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.GitHubPullRequestReviewed, StringComparison.Ordinal)
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => ReviewAsync(evt, ct);

    private async Task ReviewAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubPullRequestReviewEventPayload.Parse(evt.Data);
        if (payload is null)
        {
            _log.LogDebug("GitHub review skipped: event {EventId} carries no submitted review payload", evt.Id);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;
        if (connection.Approvers.Count == 0
            || !connection.Approvers.Contains(payload.ReviewerLogin, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        if (!GitHubPullRequestReviewTranslation.TryParseIssueNumber(payload.HeadBranch, out var issueNumber))
        {
            _log.LogDebug(
                "GitHub review skipped: branch '{Branch}' of PR #{PrNumber} does not name a Mohist issue",
                payload.HeadBranch, payload.PullRequestNumber);
            return;
        }

        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        string? workflowRunId;
        try
        {
            var status = await issueGrain.GetWorkflowStatusAsync();
            if (status?.Workflow is not { Status: "awaiting-approval", CurrentStage: "check" })
                return;
            workflowRunId = status.WorkflowRunId;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId!);
        var decidedBy = GitHubPullRequestReviewTranslation.DecidedBy(payload.ReviewerLogin);
        try
        {
            switch (payload.State)
            {
                case GitHubPullRequestReviewState.Approved:
                    await workflow.ApproveAsync(decidedBy);
                    break;
                case GitHubPullRequestReviewState.ChangesRequested:
                    await workflow.RequestChangesAsync(
                        GitHubPullRequestReviewTranslation.ChangeRequestReason(payload.Body), decidedBy);
                    break;
                default:
                    return;
            }
        }
        catch (InvalidOperationException ex)
        {
            _log.LogDebug(
                "GitHub review no-op: Check gate of issue #{IssueNumber} is no longer awaiting approval ({Message})",
                issueNumber, ex.Message);
        }
    }
}
