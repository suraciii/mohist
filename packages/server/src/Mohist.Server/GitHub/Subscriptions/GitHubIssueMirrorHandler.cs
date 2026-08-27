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
        if (link.GithubIssueNumber <= 0)
        {
            var existing = await port.FindIssueByMarkerAsync(connection, link.MirrorMarker!, ct);
            if (existing is int found)
            {
                link = await links.SetMirrorAsync(link.Id, found, ct) ?? link;
                await PostConfirmationAsync(sp, connection, link, found, issue.Number, ct);
            }
            else if (!link.MirrorCreateAttempted)
            {
                link = await links.MarkMirrorCreateAttemptedAsync(link.Id, ct) ?? link;
                try
                {
                    var created = await port.CreateIssueAsync(connection, issue.Title, issue.Body ?? string.Empty, link.MirrorMarker!, ct);
                    link = await links.SetMirrorAsync(link.Id, created, ct) ?? link;
                    await PostConfirmationAsync(sp, connection, link, created, issue.Number, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "GitHub mirror create failed for Mohist issue #{IssueNumber}", issue.Number);
                    return;
                }
            }
            return;
        }

        if (link.MirrorMarker is null) return;

        if (evt.Type is EventCatalog.ReverseDns.IssueContentChanged
            && evt.Data?.Deserialize<IssueContentChanged>(CloudEvent.JsonOptions)?.Source?.StartsWith("github:", StringComparison.Ordinal) != true)
        {
            try
            {
                await port.UpdateIssueAsync(connection, link.GithubIssueNumber, issue.Title, issue.Body ?? string.Empty, link.MirrorMarker!, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "GitHub mirror update failed for Mohist issue #{IssueNumber}", issue.Number);
            }
        }
    }

    private static async Task PostConfirmationAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        int githubIssueNumber,
        int issueNumber,
        CancellationToken ct)
    {
        if (link.HasPostedComment(GitHubCommentKinds.MirrorCreated)) return;
        await sp.GetRequiredService<IGitHubCommentPort>().PostCommentAsync(
            connection, githubIssueNumber, $"Mohist issue #{issueNumber} · linked from Mohist", ct);
        await sp.GetRequiredService<GitHubIssueLinkStore>().MarkCommentPostedAsync(
            link.Id, GitHubCommentKinds.MirrorCreated, ct);
    }
}
