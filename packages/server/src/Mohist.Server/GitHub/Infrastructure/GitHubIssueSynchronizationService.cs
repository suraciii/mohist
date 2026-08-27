using System.Net;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
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
    private readonly IGitHubIssuePort _issuePort;
    private readonly IGitHubCommentPort _commentPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubIssueSynchronizationService> _log;

    public GitHubIssueSynchronizationService(
        GitHubConnectionStore connections,
        GitHubIssueLinkStore links,
        GitHubWriteBackFailureStore failures,
        IIssueStore issues,
        IGitHubIssuePort issuePort,
        IGitHubCommentPort commentPort,
        TimeProvider timeProvider,
        ILogger<GitHubIssueSynchronizationService> log)
    {
        _connections = connections;
        _links = links;
        _failures = failures;
        _issues = issues;
        _issuePort = issuePort;
        _commentPort = commentPort;
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
                        await _issuePort.UpdateIssueAsync(
                            connection,
                            link.GithubIssueNumber,
                            issue.Title,
                            issue.Body ?? string.Empty,
                            link.MirrorMarker ?? GitHubMirrorMarker.For(link.Id),
                            ct);
                    }

                    await PostConfirmationAsync(connection, link, ct);
                    link = await _links.ClearErrorAsync(link.Id, ct) ?? link;
                    return link;
                }
                catch (HttpRequestException ex) when (
                    ex.StatusCode == HttpStatusCode.NotFound
                    && attempt == 0
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

        if (current is { IsPending: true })
        {
            if (current.MirrorCreateAttempted)
                throw new GitHubSynchronizationException("github_mirror_unknown", "The existing mirror creation intent is unresolved; reconcile it before linking another GitHub issue");
            await _links.DeleteAsync(current.Id, ct);
        }

        var claim = await _links.ClaimAsync(projectId, connection.RepositoryName, githubIssueNumber, issueNumber, ct);
        if (!claim.Won || claim.Link is null)
            throw new GitHubSynchronizationException("github_issue_already_linked", $"GitHub issue #{githubIssueNumber} is already linked");

        var link = claim.Link;
        link = await _links.EnsureMirrorMarkerAsync(link.Id, ct) ?? link;
        try
        {
            await _issuePort.UpdateIssueAsync(
                connection,
                githubIssueNumber,
                issue.Title,
                issue.Body ?? string.Empty,
                link.MirrorMarker ?? GitHubMirrorMarker.For(link.Id),
                ct);
            await PostConfirmationAsync(connection, link, ct);
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

    public async Task ReprojectConnectionAsync(GitHubConnection connection, CancellationToken ct = default)
    {
        var links = await _links.ListByConnectionAsync(connection.ProjectId, connection.RepositoryName, ct);
        foreach (var link in links)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await SyncAsync(connection.ProjectId, link.IssueNumber, pushContent: true, "mo.mohist.github.enable", ct);
            }
            catch (GitHubSynchronizationException ex)
            {
                _log.LogWarning(ex, "GitHub re-projection failed for Mohist issue #{IssueNumber}", link.IssueNumber);
            }
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

        var created = await _issuePort.CreateIssueAsync(connection, issue.Title, issue.Body ?? string.Empty, marker, ct);
        if (created <= 0)
            throw new GitHubSynchronizationException("github_mirror_number_invalid", "GitHub create issue returned an invalid issue number");
        return await _links.SetMirrorAsync(link.Id, created, ct);
    }

    private async Task PostConfirmationAsync(
        GitHubConnection connection,
        GitHubIssueLink link,
        CancellationToken ct)
    {
        if (!await _links.TryReserveCommentAsync(link.Id, GitHubCommentKinds.MirrorCreated, ct)) return;
        try
        {
            await _commentPort.PostCommentAsync(
                connection,
                link.GithubIssueNumber,
                $"Mohist issue #{link.IssueNumber} · linked from Mohist",
                ct);
            await _links.MarkCommentPostedAsync(link.Id, GitHubCommentKinds.MirrorCreated, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (!GitHubRemoteOutcome.IsUnknown(ex))
                await _links.ReleaseCommentReservationAsync(link.Id, GitHubCommentKinds.MirrorCreated, ct);
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
            await _links.MarkErrorAsync(link.Id, new GitHubSyncError(operation, code, detail, occurredAt), ct);
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
                await _connections.MarkNeedsAttentionAsync(connection.ProjectId, connection.Id, true, ct);
        }
        catch (Exception recordEx) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(recordEx, "Could not persist GitHub sync failure for link {LinkId}", link.Id);
        }
    }
}

public sealed class GitHubSynchronizationException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
