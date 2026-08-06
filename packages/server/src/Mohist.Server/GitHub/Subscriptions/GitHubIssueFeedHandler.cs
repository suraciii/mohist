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

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Feed translator: turns a GitHub <c>labeled</c> event carrying the
/// connection's intake label into a Mohist issue, then starts it or leaves
/// it in backlog per <c>FeedMode</c>. Idempotency is owned by the
/// <see cref="GitHubIssueLink"/> row — its unique index on
/// <c>(ProjectId, RepositoryName, GithubIssueNumber)</c> is the persisted
/// gate, so duplicate events, unlabel/re-label cycles, and dispatcher
/// redelivery can never create a second issue.
/// <para>
/// The link is written before the issue so a crash between the two is
/// healed on redelivery (the handler completes the linked issue number
/// instead of allocating a fresh one); an issue never exists without its
/// link. When creation fails, the just-owned link is rolled back so a
/// retry re-runs the feed cleanly.
/// </para>
/// <para>
/// Start rejection (unmet prerequisite / unavailable repository) leaves
/// the issue in backlog and posts one explanation comment through the
/// minimal comment port; the posted marker on the link keeps redelivery
/// from duplicating the comment.
/// </para>
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubIssuesLabeled,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueFeedHandler")]
public sealed class GitHubIssueFeedHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueFeedHandler> _log;

    public GitHubIssueFeedHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubIssueFeedHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && string.Equals(evt.Type, EventCatalog.ReverseDns.GitHubIssuesLabeled, StringComparison.Ordinal)
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => FeedAsync(evt, ct);

    private async Task FeedAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubIssueEventPayload.Parse(evt.Data);
        if (payload is null)
        {
            _log.LogDebug("GitHub feed skipped: event {EventId} carries no readable issue payload", evt.Id);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>().GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;
        if (!payload.Labels.Contains(connection.IntakeLabel, StringComparer.OrdinalIgnoreCase))
            return;

        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        GitHubIssueLink link;
        var owned = false;
        var issueNumber = 0;
        if (await links.GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct) is { } existing)
        {
            link = existing;
            issueNumber = existing.IssueNumber;
        }
        else
        {
            var counter = _grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId));
            var allocated = await counter.NextAsync();
            link = await links.CreateAsync(projectId, connection.RepositoryName, payload.IssueNumber, allocated, ct);
            owned = link.IssueNumber == allocated;
            issueNumber = link.IssueNumber;
        }

        var issueStore = sp.GetRequiredService<IIssueStore>();
        var issue = await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        if (issue is null)
        {
            try
            {
                await CreateIssueAsync(projectId, issueNumber, connection, payload, ct);
            }
            catch
            {
                if (owned)
                {
                    try
                    {
                        await links.DeleteAsync(link.Id, ct);
                    }
                    catch (Exception deleteEx) when (!ct.IsCancellationRequested)
                    {
                        _log.LogWarning(deleteEx,
                            "GitHub feed: could not roll back link {LinkId} after issue creation failed",
                            link.Id);
                    }
                }
                throw;
            }
            issue = await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        }

        if (issue is not null
            && string.Equals(connection.FeedMode, GitHubFeedMode.Start, StringComparison.Ordinal)
            && issue.Status == IssueStatus.Backlog)
        {
            await TryStartAsync(sp, connection, link, payload.IssueNumber, ct);
        }
    }

    private async Task CreateIssueAsync(
        string projectId,
        int issueNumber,
        GitHubConnection connection,
        GitHubIssueEventPayload payload,
        CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GitHubIssueSource.LabelKey] = GitHubIssueSource.LabelValue(
                connection.Owner, connection.Repo, payload.IssueNumber),
        };
        var priority = GitHubIssueFeedTranslation.MapPriority(payload.Labels) ?? IssuePriority.Default.Value;
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, issueNumber)));
        await grain.CreateAsync(
            projectId,
            issueNumber,
            payload.Title,
            payload.Body,
            labels,
            priority,
            repositoryRef: connection.RepositoryName,
            isDraft: false);
    }

    private async Task TryStartAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        int githubIssueNumber,
        CancellationToken ct)
    {
        var grain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(link.ProjectId, link.IssueNumber)));
        try
        {
            await grain.StartWorkAsync();
        }
        catch (IssueStartBlockedException ex)
        {
            await RejectAsync(sp, connection, link, githubIssueNumber, ex.Message, ct);
        }
        catch (IssueStartRepositoryUnavailableException ex)
        {
            await RejectAsync(sp, connection, link, githubIssueNumber, ex.Message, ct);
        }
    }

    private async Task RejectAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        int githubIssueNumber,
        string reason,
        CancellationToken ct)
    {
        if (link.HasPostedComment(GitHubCommentKinds.FeedRejected))
            return;
        try
        {
            await sp.GetRequiredService<IGitHubCommentPort>()
                .PostCommentAsync(connection, githubIssueNumber, GitHubFeedComments.Rejection(link.IssueNumber, reason), ct);
            await sp.GetRequiredService<GitHubIssueLinkStore>()
                .MarkCommentPostedAsync(link.Id, GitHubCommentKinds.FeedRejected, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex,
                "GitHub feed-rejection comment for connection {ConnectionId} issue #{IssueNumber} could not be posted; Mohist issue stays in backlog",
                connection.Id, githubIssueNumber);
        }
    }
}
