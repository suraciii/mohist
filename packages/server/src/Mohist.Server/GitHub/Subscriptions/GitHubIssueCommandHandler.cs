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
/// Translates an authorized <c>/mohist start</c> comment into one linked
/// Mohist Issue. The GitHub issue link is reserved before the Issue is
/// created, so duplicate deliveries and concurrent command handling converge
/// on the same Mohist number.
/// </summary>
[Subscription(
    Type = EventCatalog.ReverseDns.GitHubIssueCommentCreated,
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubIssueCommandHandler")]
public sealed class GitHubIssueCommandHandler : ICloudEventHandler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IGrainFactory _grains;
    private readonly ILogger<GitHubIssueCommandHandler> _log;

    public GitHubIssueCommandHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains,
        ILogger<GitHubIssueCommandHandler> log)
    {
        _scopes = scopes;
        _grains = grains;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null
        && evt.Type == EventCatalog.ReverseDns.GitHubIssueCommentCreated
        && IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out _, out _);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => HandleCoreAsync(evt, ct);

    private async Task HandleCoreAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!IngressEventPersistence.TryParseConnectionSource(evt.Source?.ToString(), out var projectId, out var connectionId))
            return;
        var payload = GitHubIssueCommentEventPayload.Parse(evt.Data);
        if (payload is null)
            return;

        var command = GitHubIssueCommand.Parse(payload.CommentBody);
        if (command is null)
            return;
        if (!GitHubIssueCommand.IsPermitted(payload.AuthorAssociation))
            return;

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var connections = sp.GetRequiredService<GitHubConnectionStore>();
        var connection = await connections.GetByIdAsync(connectionId, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;

        var comments = sp.GetRequiredService<IGitHubCommentPort>();
        if (command.Verb is GitHubIssueCommandVerb.Unknown)
        {
            await ReplyAsync(
                comments,
                connection,
                payload.IssueNumber,
                GitHubIssueCommandComments.UnknownVerb(command.RawVerb),
                marker: null,
                links: null,
                link: null,
                ct);
            return;
        }

        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        var issueStore = sp.GetRequiredService<IIssueStore>();
        var link = await links.GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        var allocated = link?.IssueNumber ?? 0;
        if (link is not null && await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, allocated))) is not null)
        {
            await ReplyAsync(
                comments,
                connection,
                payload.IssueNumber,
                GitHubIssueCommandComments.AlreadyLinked(projectId, link.IssueNumber),
                GitHubCommentKinds.CommandReply(payload.CommentId),
                links,
                link,
                ct);
            return;
        }

        if (link is null)
        {
            var counter = _grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId));
            allocated = await counter.NextAsync();
            link = await links.CreateAsync(projectId, connection.RepositoryName, payload.IssueNumber, allocated, ct);
            if (link.IssueNumber != allocated)
            {
                await ReplyAsync(
                    comments,
                    connection,
                    payload.IssueNumber,
                    GitHubIssueCommandComments.AlreadyLinked(projectId, link.IssueNumber),
                    GitHubCommentKinds.CommandReply(payload.CommentId),
                    links,
                    link,
                    ct);
                return;
            }
        }

        var issueKey = new IssueKey(projectId, allocated);
        var issueGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(issueKey));
        try
        {
            await issueGrain.CreateAsync(
                projectId,
                allocated,
                payload.Title,
                payload.Body,
                labels: null,
                GitHubIssueCommandTranslation.MapPriority(payload.Labels),
                repositoryRef: connection.RepositoryName,
                isDraft: false);
        }
        catch (InvalidOperationException)
        {
            if (await issueStore.LoadAsync(GrainKey.Issue(issueKey)) is null)
                throw;
            // A previous delivery may have committed the Issue before its
            // response was lost. Continue with the existing aggregate.
        }

        try
        {
            await issueGrain.StartWorkAsync();
            await ReplyAsync(
                comments,
                connection,
                payload.IssueNumber,
                GitHubIssueCommandComments.Started(projectId, allocated),
                GitHubCommentKinds.CommandReply(payload.CommentId),
                links,
                link,
                ct);
        }
        catch (IssueStartBlockedException ex)
        {
            await ReplyAsync(
                comments,
                connection,
                payload.IssueNumber,
                GitHubIssueCommandComments.StartFailed(ex.Message),
                GitHubCommentKinds.CommandReply(payload.CommentId),
                links,
                link,
                ct);
        }
        catch (IssueStartRepositoryUnavailableException ex)
        {
            await ReplyAsync(
                comments,
                connection,
                payload.IssueNumber,
                GitHubIssueCommandComments.StartFailed(ex.Message),
                GitHubCommentKinds.CommandReply(payload.CommentId),
                links,
                link,
                ct);
        }
    }

    private async Task ReplyAsync(
        IGitHubCommentPort comments,
        GitHubConnection connection,
        int githubIssueNumber,
        string body,
        string? marker,
        GitHubIssueLinkStore? links,
        GitHubIssueLink? link,
        CancellationToken ct)
    {
        if (marker is not null && link?.HasPostedComment(marker) == true)
            return;
        try
        {
            await comments.PostCommentAsync(connection, githubIssueNumber, body, ct);
            if (marker is not null && links is not null && link is not null)
                await links.MarkCommentPostedAsync(link.Id, marker, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(
                ex,
                "GitHub command reply for {Owner}/{Repo} issue #{GithubIssueNumber} could not be posted",
                connection.Owner,
                connection.Repo,
                githubIssueNumber);
        }
    }
}
