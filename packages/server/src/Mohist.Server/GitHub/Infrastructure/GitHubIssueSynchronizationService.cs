using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Domain.Events;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Workflow.Domain.Run;
using DomainIssue = Mohist.Server.Issue.Domain.Issue;

namespace Mohist.Server.GitHub.Infrastructure;

/// <summary>
/// Reconciles one Mohist Issue and its GitHub mirror. This is deliberately a
/// synchronous service: the durable link is the operation record and callers
/// can retry the same projection without introducing an outbox.
/// </summary>
public sealed class GitHubIssueSynchronizationService : IScopedService
{
    private readonly GitHubConnectionStore _connections;
    private readonly GitHubIssueLinkStore _links;
    private readonly GitHubWriteBackFailureStore _failures;
    private readonly IIssueStore _issues;
    private readonly IWorkflowRunStore _workflowRuns;
    private readonly IEventStore _events;
    private readonly IGitHubIssuePort _issuePort;
    private readonly IGitHubCommentPort _commentPort;
    private readonly GitHubConnectionGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubIssueSynchronizationService> _log;

    public GitHubIssueSynchronizationService(
        GitHubConnectionStore connections,
        GitHubIssueLinkStore links,
        GitHubWriteBackFailureStore failures,
        IIssueStore issues,
        IWorkflowRunStore workflowRuns,
        IEventStore events,
        IGitHubIssuePort issuePort,
        IGitHubCommentPort commentPort,
        GitHubConnectionGate gate,
        TimeProvider timeProvider,
        ILogger<GitHubIssueSynchronizationService> log)
    {
        _connections = connections;
        _links = links;
        _failures = failures;
        _issues = issues;
        _workflowRuns = workflowRuns;
        _events = events;
        _issuePort = issuePort;
        _commentPort = commentPort;
        _gate = gate;
        _timeProvider = timeProvider;
        _log = log;
    }

    public async Task<GitHubIssueLink?> SyncAsync(
        string projectId,
        int issueNumber,
        bool pushContent = true,
        string eventType = "mo.mohist.github.sync",
        CancellationToken ct = default)
    {
        var issue = await _issues.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        if (issue is null)
            throw new GitHubSynchronizationException("issue_not_found", $"Issue #{issueNumber} not found");

        var connection = await _connections.GetByRepositoryAsync(projectId, issue.RepositoryRef ?? string.Empty, ct);
        if (connection is null)
            throw new GitHubSynchronizationException("github_connection_not_found", "Issue target repository has no GitHub connection");
        if (connection.Status != GitHubConnectionStatus.Active)
            throw new GitHubSynchronizationException("github_connection_disabled", "GitHub connection is disabled");
        if (issue.IsDraft)
            throw new GitHubSynchronizationException("issue_is_draft", "Draft Issues do not have a GitHub mirror");

        var link = await _links.GetByIssueAsync(projectId, issueNumber, ct);
        if (link is null)
            link = await _links.CreatePendingAsync(projectId, connection.RepositoryName, issueNumber, ct);
        var linkId = link.Id;

        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    link = await EnsureMirrorAsync(connection, issue, link, eventType, ct);
                    if (link is null || link.IsPending)
                        throw new GitHubSynchronizationException("github_mirror_pending", "GitHub mirror is still awaiting reconciliation");

                    link = await _links.EnsureMirrorMarkerAsync(link.Id, ct) ?? link;
                    if (pushContent)
                    {
                        await UpdateMirrorContentAsync(connection, link, issue, ct);
                    }

                    await PostConfirmationAsync(connection, link, ct);
                    await ProjectCurrentStateAsync(connection, issue, link, ct);
                    link = await _links.ClearErrorAsync(link.Id, ct) ?? link;
                    return link;
                }
                catch (GitHubMirrorNotFoundException) when (
                    attempt == 0
                    && !ct.IsCancellationRequested)
                {
                    var staleNumber = link?.GithubIssueNumber ?? 0;
                    if (staleNumber <= 0)
                        throw;
                    var current = await _links.GetByIdAsync(linkId, ct);
                    if (current is null || current.GithubIssueNumber != staleNumber)
                        throw;
                    link = await _links.ResetMirrorAsync(linkId, staleNumber, ct) ?? current;
                }
            }

            throw new GitHubSynchronizationException("github_sync_failed", "GitHub synchronization did not converge after recreating a missing mirror");
        }
        catch (GitHubSynchronizationException ex) when (!ct.IsCancellationRequested)
        {
            var operation = link?.IsPending == true ? GitHubWriteBackOperation.Reconcile : GitHubWriteBackOperation.Content;
            await RecordFailureAsync(connection, link, issueNumber, eventType, operation, ex, ct);
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var operation = link?.IsPending == true ? GitHubWriteBackOperation.Reconcile : GitHubWriteBackOperation.Content;
            await RecordFailureAsync(connection, link, issueNumber, eventType, operation, ex, ct);
            throw new GitHubSynchronizationException(
                "github_sync_failed",
                ex.Message,
                ex);
        }
    }

    public async Task<GitHubIssueLink> LinkAsync(
        string projectId,
        int issueNumber,
        string owner,
        string repo,
        int githubIssueNumber,
        CancellationToken ct = default)
    {
        var issue = await _issues.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        if (issue is null)
            throw new GitHubSynchronizationException("issue_not_found", $"Issue #{issueNumber} not found");

        var connection = (await _connections.ListAsync(projectId, ct))
            .FirstOrDefault(item => string.Equals(item.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (connection is null)
            throw new GitHubSynchronizationException("github_connection_not_found", $"GitHub repository '{owner}/{repo}' is not connected to this project");
        if (connection.Status != GitHubConnectionStatus.Active)
            throw new GitHubSynchronizationException("github_connection_disabled", "GitHub connection is disabled");

        var current = await _links.GetByIssueAsync(projectId, issueNumber, ct);
        if (current is { IsPending: false })
            throw new GitHubSynchronizationException("github_issue_already_linked", $"Issue #{issueNumber} is already linked");
        var target = await _links.GetAsync(projectId, connection.RepositoryName, githubIssueNumber, ct);
        if (target is not null)
            throw new GitHubSynchronizationException("github_issue_already_linked", $"GitHub issue #{githubIssueNumber} is already linked");

        var snapshot = await _issuePort.GetIssueAsync(connection, githubIssueNumber, ct);
        if (snapshot is null)
            throw new GitHubSynchronizationException("github_issue_not_found", $"GitHub issue #{githubIssueNumber} not found");

        GitHubIssueLinkClaim claim;
        if (current is { IsPending: true })
        {
            var pendingClaim = await _links.TryClaimPendingForManualLinkAsync(current.Id, githubIssueNumber, ct);
            if (pendingClaim is null || !pendingClaim.Won || pendingClaim.Link is null)
            {
                var latest = await _links.GetByIdAsync(current.Id, ct);
                var code = latest?.MirrorCreateAttempted == true
                    ? "github_mirror_unknown"
                    : "github_issue_already_linked";
                throw new GitHubSynchronizationException(
                    code,
                    code == "github_mirror_unknown"
                        ? "The existing mirror creation intent is unresolved; reconcile it before linking another GitHub issue"
                        : $"GitHub issue #{githubIssueNumber} is already linked");
            }
            claim = pendingClaim;
        }
        else
        {
            claim = await _links.ClaimAsync(
                projectId,
                connection.RepositoryName,
                githubIssueNumber,
                issueNumber,
                ct);
            if (!claim.Won || claim.Link is null)
                throw new GitHubSynchronizationException("github_issue_already_linked", $"GitHub issue #{githubIssueNumber} is already linked");
        }

        var link = claim.Link;
        link = await _links.EnsureMirrorMarkerAsync(link.Id, ct) ?? link;
        try
        {
            await SendInsideGateAsync(connection, (current, token) =>
                _issuePort.UpdateIssueAsync(
                    current,
                    githubIssueNumber,
                    issue.Title,
                    issue.Body ?? string.Empty,
                    link.MirrorMarker ?? GitHubMirrorMarker.For(link.Id),
                    token), ct);
            await PostConfirmationAsync(connection, link, ct);
            await ProjectCurrentStateAsync(connection, issue, link, ct);
            return await _links.ClearErrorAsync(link.Id, ct) ?? link;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(connection, link, issueNumber, "mo.mohist.github.link", GitHubWriteBackOperation.Link, ex, ct);
            throw new GitHubSynchronizationException("github_link_failed", ex.Message, ex);
        }
    }

    public async Task<bool> UnlinkAsync(string projectId, int issueNumber, CancellationToken ct = default)
    {
        var link = await _links.GetByIssueAsync(projectId, issueNumber, ct);
        if (link is null) return false;
        await _links.DeleteAsync(link.Id, ct);
        return true;
    }

    public async Task<bool> ReprojectConnectionAsync(GitHubConnection connection, CancellationToken ct = default)
    {
        var links = await _links.ListByConnectionAsync(connection.ProjectId, connection.RepositoryName, ct);
        var succeeded = true;
        foreach (var link in links)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                await SyncAsync(connection.ProjectId, link.IssueNumber, pushContent: true, "mo.mohist.github.enable", ct);
            }
            catch (GitHubSynchronizationException ex)
            {
                succeeded = false;
                _log.LogWarning(ex, "GitHub re-projection failed for Mohist issue #{IssueNumber}", link.IssueNumber);
            }
        }

        if (succeeded && !ct.IsCancellationRequested)
            await _connections.ClearReprojectionPendingAsync(connection.ProjectId, connection.Id, ct);
        return succeeded;
    }

    /// <summary>
    /// Send fence shared by every outbound projection: runs inside the
    /// connection gate and re-reads the connection, so a Disable that wins
    /// the gate stops the send. Callers treat the thrown
    /// <c>github_connection_disabled</c> as retain-and-stop, never retry
    /// accounting.
    /// </summary>
    private async Task<T> SendInsideGateAsync<T>(
        GitHubConnection connection,
        Func<GitHubConnection, CancellationToken, Task<T>> send,
        CancellationToken ct)
    {
        return await _gate.EnterAsync(connection.Id, async token =>
        {
            var current = await _connections.GetByIdAsync(connection.Id, token);
            if (current is null || current.Status != GitHubConnectionStatus.Active)
                throw new GitHubSynchronizationException(
                    "github_connection_disabled",
                    "GitHub connection is disabled");
            return await send(current, token);
        }, ct);
    }

    private async Task SendInsideGateAsync(
        GitHubConnection connection,
        Func<GitHubConnection, CancellationToken, Task> send,
        CancellationToken ct)
    {
        await SendInsideGateAsync<object?>(connection, async (current, token) =>
        {
            await send(current, token);
            return null;
        }, ct);
    }

    private async Task UpdateMirrorContentAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        DomainIssue issue,
        CancellationToken ct)
    {
        try
        {
            await SendInsideGateAsync(connection, (current, token) =>
                _issuePort.UpdateIssueAsync(
                    current,
                    link.GithubIssueNumber,
                    issue.Title,
                    issue.Body ?? string.Empty,
                    link.MirrorMarker ?? GitHubMirrorMarker.For(link.Id),
                    token), ct);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == HttpStatusCode.NotFound
            && !ct.IsCancellationRequested)
        {
            // Only the exact mirror content endpoint is allowed to trigger
            // mirror identity recovery. Comment, label, close, and PR
            // endpoints may also return 404 but do not prove the mirror is
            // gone.
            throw new GitHubMirrorNotFoundException(link.GithubIssueNumber, ex);
        }
    }

    private async Task<GitHubIssueLink?> EnsureMirrorAsync(
        GitHubConnection connection,
        DomainIssue issue,
        GitHubIssueLink link,
        string eventType,
        CancellationToken ct)
    {
        if (!link.IsPending)
            return link;
        // The whole reconcile-or-create sequence runs inside the send fence:
        // probe, reservation, and create observe one connection status, so a
        // Disable either precedes the probe or waits for the settled mirror.
        return await SendInsideGateAsync(connection, (
            current, token) => EnsureMirrorCoreAsync(current, issue, link, eventType, token), ct);
    }

    private async Task<GitHubIssueLink?> EnsureMirrorCoreAsync(
        GitHubConnection connection,
        DomainIssue issue,
        GitHubIssueLink link,
        string eventType,
        CancellationToken ct)
    {
        var marker = link.MirrorMarker;
        if (string.IsNullOrWhiteSpace(marker))
            throw new GitHubSynchronizationException("github_mirror_marker_missing", "Pending GitHub mirror has no reconciliation marker");

        var existing = await _issuePort.FindIssueByMarkerAsync(connection, marker, ct);
        if (existing is int found)
        {
            if (found <= 0)
                throw new GitHubSynchronizationException("github_mirror_number_invalid", "GitHub mirror reconciliation returned an invalid issue number");
            return await _links.SetMirrorAsync(link.Id, found, ct);
        }
        if (link.MirrorCreateAttempted)
            throw new GitHubSynchronizationException("github_mirror_unknown", "GitHub mirror creation remains unresolved; inspect GitHub and retry reconciliation");

        var reservation = await _links.TryReserveMirrorCreateAsync(link.Id, ct);
        if (reservation is null)
            throw new GitHubSynchronizationException("github_mirror_missing", "GitHub mirror link disappeared during reconciliation");
        if (!reservation.Acquired)
        {
            if (!reservation.Link.IsPending)
                return reservation.Link;
            throw new GitHubSynchronizationException("github_mirror_unknown", "GitHub mirror creation is reserved by another synchronization attempt");
        }

        int created;
        try
        {
            created = await _issuePort.CreateIssueAsync(connection, issue.Title, issue.Body ?? string.Empty, marker, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !GitHubRemoteOutcome.IsUnknown(ex))
        {
            // A known rejection did not create a remote issue, so release the
            // reservation and let the next explicit sync retry the POST.
            await _links.ResetMirrorCreateAttemptAsync(link.Id, ct);
            throw;
        }

        if (created <= 0)
        {
            // A successful HTTP response with no usable identity is still an
            // unknown remote outcome. Releasing the reservation would permit
            // a duplicate POST when GitHub actually created the Issue.
            throw new GitHubRemoteOutcomeUnknownException(
                "GitHub create issue response did not contain a valid issue number");
        }
        return await _links.SetMirrorAsync(link.Id, created, ct);
    }

    private async Task ProjectCurrentStateAsync(
        GitHubConnection connection,
        DomainIssue issue,
        GitHubIssueLink link,
        CancellationToken ct)
    {
        var workflow = issue.WorkflowRunId is { } workflowRunId
            ? await _workflowRuns.LoadAsync(workflowRunId, ct)
            : null;
        var stateLabel = ResolveStateLabel(issue, workflow);
        if (stateLabel is not null)
            await SetStateLabelAsync(connection, link, stateLabel, ct);

        if (issue.HasWorkflowStarted)
        {
            await PostCommentAsync(
                connection,
                link,
                GitHubCommentKinds.WorkStarted,
                GitHubWriteBackComments.WorkStarted(link.IssueNumber),
                ct);
        }

        if (workflow?.Stages.Any(stage => stage.ApprovalStatus is not null) == true)
        {
            await PostCommentAsync(
                connection,
                link,
                GitHubCommentKinds.ApprovalRequested,
                GitHubWriteBackComments.ApprovalRequested(link.IssueNumber),
                ct);
        }

        switch (issue.Status)
        {
            case IssueStatus.Done:
                var pullRequestUrl = await _commentPort.FindDeliveryPullRequestUrlAsync(
                    connection,
                    link.IssueNumber,
                    ct);
                await PostCommentAsync(
                    connection,
                    link,
                    GitHubCommentKinds.Completed,
                    GitHubWriteBackComments.Completed(link.IssueNumber, pullRequestUrl),
                    ct);
                await CloseAsync(connection, link, "completed", GitHubCommentKinds.ClosedCompleted, ct);
                break;
            case IssueStatus.Cancelled:
                var reason = await ReadCancellationReasonAsync(issue, ct);
                await PostCommentAsync(
                    connection,
                    link,
                    GitHubCommentKinds.Cancelled,
                    GitHubWriteBackComments.Cancelled(link.IssueNumber, reason),
                    ct);
                await CloseAsync(connection, link, "not_planned", GitHubCommentKinds.ClosedNotPlanned, ct);
                break;
        }
    }

    private static string? ResolveStateLabel(DomainIssue issue, WorkflowRun? workflow) =>
        issue.Status switch
        {
            IssueStatus.Done => GitHubStateLabels.Done,
            IssueStatus.InProgress when workflow?.Status == WorkflowRunStatus.AwaitingApproval
                => GitHubStateLabels.AwaitingApproval,
            IssueStatus.InProgress when workflow?.Status == WorkflowRunStatus.Failed
                => GitHubStateLabels.Blocked,
            IssueStatus.InProgress => GitHubStateLabels.InProgress,
            _ => null,
        };

    private async Task<string?> ReadCancellationReasonAsync(DomainIssue issue, CancellationToken ct)
    {
        var events = await _events.ListIssueEventsAsync(issue.ProjectId, issue.Number, limit: 200, ct);
        foreach (var stored in events.OrderByDescending(item => item.Id))
        {
            if (stored.Envelope.Type != EventCatalog.ReverseDns.IssueCancelled)
                continue;
            return stored.Envelope.Data?.Deserialize<IssueCancelled>(CloudEvent.JsonOptions)?.Reason;
        }
        return null;
    }

    private async Task PostConfirmationAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        CancellationToken ct) =>
        await PostCommentAsync(
            connection,
            link,
            GitHubCommentKinds.MirrorCreated,
            $"Mohist issue #{link.IssueNumber} · linked from Mohist",
            ct);

    private async Task PostCommentAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        string commentKey,
        string body,
        CancellationToken ct)
    {
        var marker = GitHubCommentOperationMarker.For(link.Id, commentKey);
        if (!await _links.TryReserveCommentAsync(
            link.Id,
            commentKey,
            GitHubCommentOperationKind.Comment,
            body,
            stateReason: null,
            ct))
        {
            var current = await _links.GetByIdAsync(link.Id, ct);
            if (current?.HasPostedComment(commentKey) == true)
                return;
            throw new GitHubSynchronizationException(
                "github_comment_pending",
                $"GitHub comment operation '{commentKey}' is still reserved; reconcile it before retrying");
        }

        try
        {
            await SendInsideGateAsync(connection, async (current, token) =>
            {
                await _commentPort.PostCommentAsync(
                    current,
                    link.GithubIssueNumber,
                    GitHubMirrorMarker.Append(body, marker),
                    token);
                await _links.MarkCommentPostedAsync(link.Id, commentKey, link.GithubIssueNumber, token);
            }, ct);
        }
        catch (GitHubSynchronizationException ex) when (
            ex.Code == "github_connection_disabled"
            && !ct.IsCancellationRequested)
        {
            // Disabled won the fence: retain the reservation for enable
            // recovery instead of recording a retry.
            await _links.ReleaseCommentOperationLeaseAsync(link.Id, commentKey, ct);
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (GitHubRemoteOutcome.IsUnknown(ex))
                await _links.DeferCommentOperationAsync(link.Id, commentKey, ex.Message, ct);
            else
                await _links.ReleaseCommentReservationAsync(link.Id, commentKey, ct);
            throw;
        }
    }

    private async Task SetStateLabelAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        string stateLabel,
        CancellationToken ct)
    {
        if (string.Equals(link.StateLabel, stateLabel, StringComparison.Ordinal))
            return;
        // Idempotent replacement: a Disable that wins the fence stops this
        // projection; the next event, explicit sync, or enable reprojection
        // sends the current label again.
        await SendInsideGateAsync(connection, (current, token) =>
            _commentPort.ReplaceStateLabelAsync(current, link.GithubIssueNumber, stateLabel, token), ct);
        await _links.SetStateLabelAsync(link.Id, stateLabel, ct);
    }

    private async Task CloseAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        string stateReason,
        string closeKey,
        CancellationToken ct)
    {
        if (!await _links.TryReserveCommentAsync(
            link.Id,
            closeKey,
            GitHubCommentOperationKind.Close,
            body: null,
            stateReason: stateReason,
            ct: ct))
        {
            var current = await _links.GetByIdAsync(link.Id, ct);
            if (current?.HasPostedComment(closeKey) == true)
                return;
            throw new GitHubSynchronizationException(
                "github_close_pending",
                $"GitHub close operation '{closeKey}' is still reserved; reconcile it before retrying");
        }

        try
        {
            await SendInsideGateAsync(connection, async (current, token) =>
            {
                await _commentPort.CloseIssueAsync(
                    current,
                    link.GithubIssueNumber,
                    stateReason,
                    token);
                await _links.MarkCommentPostedAsync(link.Id, closeKey, link.GithubIssueNumber, token);
            }, ct);
        }
        catch (GitHubSynchronizationException ex) when (
            ex.Code == "github_connection_disabled"
            && !ct.IsCancellationRequested)
        {
            await _links.ReleaseCommentOperationLeaseAsync(link.Id, closeKey, ct);
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (GitHubRemoteOutcome.IsUnknown(ex))
                await _links.DeferCommentOperationAsync(link.Id, closeKey, ex.Message, ct);
            else
                await _links.ReleaseCommentReservationAsync(link.Id, closeKey, ct);
            throw;
        }
    }

    private async Task RecordFailureAsync(
        GitHubConnection connection,
        GitHubIssueLink? link,
        int issueNumber,
        string eventType,
        string operation,
        Exception ex,
        CancellationToken ct)
    {
        if (link is null) return;
        var code = ex is HttpRequestException { StatusCode: { } status }
            ? ((int)status).ToString()
            : ex.GetType().Name;
        var detail = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
        var occurredAt = _timeProvider.GetUtcNow();
        try
        {
            await _failures.CreateAsync(new GitHubWriteBackFailure
            {
                ProjectId = connection.ProjectId,
                ConnectionId = connection.Id,
                RepositoryName = connection.RepositoryName,
                GithubIssueNumber = link.GithubIssueNumber,
                IssueNumber = issueNumber,
                EventType = eventType,
                Operation = operation,
                ErrorCode = code,
                ErrorDetail = detail,
                CreatedAt = occurredAt,
            }, ct);
            await _links.MarkErrorAsync(
                link.Id,
                new GitHubSyncError(operation, code, detail, occurredAt),
                expectedGithubIssueNumber: link.GithubIssueNumber > 0
                    ? link.GithubIssueNumber
                    : null,
                ct: ct);
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                && !GitHubRemoteOutcome.IsRateLimited(ex))
                await _connections.MarkNeedsAttentionAsync(connection.ProjectId, connection.Id, true, ct);
        }
        catch (Exception recordEx) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(recordEx, "Could not persist GitHub sync failure for link {LinkId}", link.Id);
        }
    }
}

public class GitHubSynchronizationException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class GitHubMirrorNotFoundException(
    int githubIssueNumber,
    Exception inner)
    : GitHubSynchronizationException(
        "github_mirror_not_found",
        $"GitHub mirror issue #{githubIssueNumber} was not found",
        inner);
