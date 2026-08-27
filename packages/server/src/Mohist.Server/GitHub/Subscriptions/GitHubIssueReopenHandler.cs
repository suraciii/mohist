using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Grains.Coordinator;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Translates GitHub reopen events for linked Issues. A cancelled Issue is
/// reopened through the Project coordinator, while a Done Issue remains
/// terminal and receives one durable follow-up suggestion.
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubIssuesReopened,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueReopenHandler")]
public sealed class GitHubIssueReopenHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueReopenHandler> _log;

    public GitHubIssueReopenHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubIssueReopenHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.GitHubIssuesReopened, StringComparison.Ordinal)
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => ReopenAsync(evt, ct);

    private async Task ReopenAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubIssueEventPayload.Parse(evt.Data);
        if (payload is null)
            return;

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;

        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        var link = await links.GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        if (link is null || link.IsPending)
            return;

        var issue = await sp.GetRequiredService<IIssueStore>()
            .LoadAsync(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
        if (issue is null)
            return;

        if (issue.Status == IssueStatus.Done)
        {
            await PostFollowUpAsync(sp, connection, link, ct);
            return;
        }
        if (issue.Status != IssueStatus.Cancelled)
            return;

        var coordinator = _grains.GetGrain<IIssueRepositoryCoordinatorGrain>(projectId);
        try
        {
            var result = await coordinator.ReopenAsync(
                new RepositoryCommandPayload.Reopen(
                    ProjectId: projectId,
                    IssueNumber: link.IssueNumber,
                    RepositoryName: link.RepositoryName),
                commandId: $"github-reopen:{connectionId}:{payload.IssueNumber}:{evt.Id}",
                expectedRevision: null);
            if (!result.IsApplied)
            {
                _log.LogDebug(
                    "GitHub reopen for Mohist issue #{IssueNumber} was rejected ({Code}): {Message}",
                    link.IssueNumber, result.Code, result.Message);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "GitHub reopen failed for Mohist issue #{IssueNumber}", link.IssueNumber);
        }
    }

    private static async Task PostFollowUpAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        CancellationToken ct)
    {
        if (link.HasPostedComment(GitHubCommentKinds.ReopenedDoneFollowUp))
            return;

        await sp.GetRequiredService<IGitHubCommentPort>().PostCommentAsync(
            connection,
            link.GithubIssueNumber,
            GitHubWriteBackComments.ReopenedDoneFollowUp(link.IssueNumber),
            ct);
        await sp.GetRequiredService<GitHubIssueLinkStore>().MarkCommentPostedAsync(
            link.Id,
            GitHubCommentKinds.ReopenedDoneFollowUp,
            ct);
    }
}
