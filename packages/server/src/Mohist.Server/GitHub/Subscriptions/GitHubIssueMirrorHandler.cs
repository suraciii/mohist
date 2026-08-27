using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Maintains the passive Mohist-to-GitHub mirror. The link is persisted before
/// the external POST so a crash or unknown response can reconcile by marker.
/// </summary>
[Subscription(
    Type = "com.mohist.issue.created|com.mohist.issue.content-changed|com.mohist.issue.draft-changed",
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueMirrorHandler")]
public sealed class GitHubIssueMirrorHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GitHubIssueMirrorHandler> _log;

    public GitHubIssueMirrorHandler(IServiceScopeFactory scopes, ILogger<GitHubIssueMirrorHandler> log)
    { _scopes = scopes; _log = log; }

    public bool Filter(CloudEvent evt) => evt is not null
        && evt.Type is EventCatalog.ReverseDns.IssueCreated
            or EventCatalog.ReverseDns.IssueContentChanged
            or EventCatalog.ReverseDns.IssueDraftChanged;

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => HandleCoreAsync(evt, ct);

    private async Task HandleCoreAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt, out var context)) return;
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var issues = sp.GetRequiredService<IIssueStore>();
        var issue = await issues.LoadAsync(GrainKey.Issue(new IssueKey(context.ProjectId, context.IssueNumber)));
        if (issue is null || issue.IsDraft) return;
        var connections = sp.GetRequiredService<GitHubConnectionStore>();
        var connection = await connections.GetByRepositoryAsync(context.ProjectId, issue.RepositoryRef ?? string.Empty, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active) return;

        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        var link = await links.GetByIssueAsync(context.ProjectId, issue.Number, ct);
        if (link is null)
        {
            link = await links.CreatePendingAsync(context.ProjectId, issue.RepositoryRef!, issue.Number, ct);
        }
        var port = sp.GetRequiredService<IGitHubIssuePort>();
        if (link.IsPending)
        {
            var marker = link.MirrorMarker;
            if (string.IsNullOrWhiteSpace(marker))
            {
                _log.LogWarning(
                    "GitHub mirror link {LinkId} for Mohist issue #{IssueNumber} has no reconciliation marker",
                    link.Id, issue.Number);
                return;
            }

            int? existing;
            try
            {
                existing = await port.FindIssueByMarkerAsync(connection, marker, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    ex,
                    "GitHub mirror marker lookup failed for Mohist issue #{IssueNumber}",
                    issue.Number);
                return;
            }

            if (existing is int found)
            {
                if (found <= 0)
                {
                    _log.LogWarning(
                        "GitHub mirror marker lookup returned invalid issue number {GithubIssueNumber} for Mohist issue #{IssueNumber}",
                        found, issue.Number);
                    return;
                }

                try
                {
                    var linked = await links.SetMirrorAsync(link.Id, found, ct);
                    if (linked is null)
                    {
                        _log.LogWarning(
                            "GitHub mirror link {LinkId} disappeared before setting issue #{GithubIssueNumber}",
                            link.Id, found);
                        return;
                    }
                    link = linked;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(
                        ex,
                        "GitHub mirror link {LinkId} could not adopt issue #{GithubIssueNumber}; reconciliation failed closed",
                        link.Id, found);
                    return;
                }

                await TryPostConfirmationAsync(sp, connection, link, found, issue.Number, ct);
            }
            else if (!link.MirrorCreateAttempted)
            {
                try
                {
                    var marked = await links.MarkMirrorCreateAttemptedAsync(link.Id, ct);
                    if (marked is null)
                    {
                        _log.LogWarning(
                            "GitHub mirror link {LinkId} disappeared before create attempt for Mohist issue #{IssueNumber}",
                            link.Id, issue.Number);
                        return;
                    }
                    link = marked;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(
                        ex,
                        "GitHub mirror creation intent could not be marked for Mohist issue #{IssueNumber}",
                        issue.Number);
                    return;
                }

                if (!link.IsPending)
                {
                    await TryPostConfirmationAsync(sp, connection, link, link.GithubIssueNumber, issue.Number, ct);
                    return;
                }

                int created;
                try
                {
                    created = await port.CreateIssueAsync(
                        connection, issue.Title, issue.Body ?? string.Empty, marker, ct);
                    if (created <= 0)
                    {
                        _log.LogWarning(
                            "GitHub mirror create returned invalid issue number {GithubIssueNumber} for Mohist issue #{IssueNumber}",
                            created, issue.Number);
                        return;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "GitHub mirror create failed for Mohist issue #{IssueNumber}", issue.Number);
                    return;
                }

                try
                {
                    var linked = await links.SetMirrorAsync(link.Id, created, ct);
                    if (linked is null)
                    {
                        _log.LogWarning(
                            "GitHub mirror link {LinkId} disappeared after creating issue #{GithubIssueNumber}",
                            link.Id, created);
                        return;
                    }
                    link = linked;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(
                        ex,
                        "GitHub mirror link {LinkId} could not record created issue #{GithubIssueNumber}; reconciliation failed closed",
                        link.Id, created);
                    return;
                }

                await TryPostConfirmationAsync(sp, connection, link, created, issue.Number, ct);
            }
            return;
        }

        if (link.MirrorMarker is null) return;

        // Revisit the confirmation gate on every later mirror event. The
        // posted-comment set is the durable success marker; a failed post does
        // not block this event and a later event can retry it.
        await TryPostConfirmationAsync(sp, connection, link, link.GithubIssueNumber, issue.Number, ct);

        if (evt.Type is EventCatalog.ReverseDns.IssueContentChanged
            && evt.Data?.Deserialize<IssueContentChanged>(CloudEvent.JsonOptions)?.Source?.StartsWith("github:", StringComparison.Ordinal) != true)
        {
            try
            {
                await port.UpdateIssueAsync(connection, link.GithubIssueNumber, issue.Title, issue.Body ?? string.Empty, link.MirrorMarker, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "GitHub mirror update failed for Mohist issue #{IssueNumber}", issue.Number);
            }
        }
    }

    private async Task TryPostConfirmationAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        int githubIssueNumber,
        int issueNumber,
        CancellationToken ct)
    {
        if (link.HasPostedComment(GitHubCommentKinds.MirrorCreated)) return;

        try
        {
            await sp.GetRequiredService<IGitHubCommentPort>().PostCommentAsync(
                connection, githubIssueNumber, $"Mohist issue #{issueNumber} · linked from Mohist", ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(
                ex,
                "GitHub mirror confirmation for Mohist issue #{IssueNumber} could not be posted",
                issueNumber);
            return;
        }

        try
        {
            await sp.GetRequiredService<GitHubIssueLinkStore>().MarkCommentPostedAsync(
                link.Id, GitHubCommentKinds.MirrorCreated, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(
                ex,
                "GitHub mirror confirmation bookkeeping for link {LinkId} could not be persisted",
                link.Id);
        }
    }
}
