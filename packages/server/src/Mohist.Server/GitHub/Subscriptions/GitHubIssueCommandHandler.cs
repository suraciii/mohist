using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
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

    public GitHubIssueCommandHandler(
        IServiceScopeFactory scopes,
        IGrainFactory grains)
    {
        _scopes = scopes;
        _grains = grains;
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

        var replies = sp.GetRequiredService<GitHubCommandReplyStore>();
        var delivery = sp.GetRequiredService<GitHubCommandReplyDeliveryService>();
        if (command.Verb is GitHubIssueCommandVerb.Unknown)
        {
            await ReplyAsync(
                replies,
                delivery,
                projectId,
                connection,
                payload.IssueNumber,
                payload.CommentId,
                GitHubCommentKinds.CommandReplyUnknownVerb,
                GitHubIssueCommandComments.UnknownVerb(command.RawVerb),
                ct);
            return;
        }

        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        var issueStore = sp.GetRequiredService<IIssueStore>();
        var link = await links.GetAsync(projectId, connection.RepositoryName, payload.IssueNumber, ct);
        var allocated = link?.IssueNumber ?? 0;
        var existingIssue = link is not null
            ? await issueStore.LoadAsync(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)))
            : null;
        if (link is not null && existingIssue is not null)
        {
            var existingGrain = _grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(projectId, link.IssueNumber)));
            if (link.CommandRequested && existingIssue.Status == IssueStatus.Backlog)
            {
                await StartAndReplyAsync(
                    replies,
                    delivery,
                    projectId,
                    connection,
                    payload,
                    existingGrain,
                    link.IssueNumber,
                    ct);
            }
            else if (link.CommandRequested && existingIssue.Status == IssueStatus.InProgress)
            {
                await ReplyAsync(
                    replies,
                    delivery,
                    projectId,
                    connection,
                    payload.IssueNumber,
                    payload.CommentId,
                    GitHubCommentKinds.CommandReplyStarted,
                    GitHubIssueCommandComments.Started(projectId, link.IssueNumber),
                    ct);
            }
            else
            {
                await ReplyAsync(
                    replies,
                    delivery,
                    projectId,
                    connection,
                    payload.IssueNumber,
                    payload.CommentId,
                    GitHubCommentKinds.CommandReplyAlreadyLinked,
                    GitHubIssueCommandComments.AlreadyLinked(projectId, link.IssueNumber),
                    ct);
            }
            return;
        }

        if (link is null)
        {
            var counter = _grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(projectId));
            allocated = await counter.NextAsync();
            link = await links.CreateAsync(
                projectId,
                connection.RepositoryName,
                payload.IssueNumber,
                allocated,
                commandRequested: true,
                ct: ct);
            if (link.IssueNumber != allocated)
            {
                await ReplyAsync(
                    replies,
                    delivery,
                    projectId,
                    connection,
                    payload.IssueNumber,
                    payload.CommentId,
                    GitHubCommentKinds.CommandReplyAlreadyLinked,
                    GitHubIssueCommandComments.AlreadyLinked(projectId, link.IssueNumber),
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

        await StartAndReplyAsync(
            replies,
            delivery,
            projectId,
            connection,
            payload,
            issueGrain,
            allocated,
            ct);
    }

    private async Task StartAndReplyAsync(
        GitHubCommandReplyStore replies,
        GitHubCommandReplyDeliveryService delivery,
        string projectId,
        GitHubConnection connection,
        GitHubIssueCommentEventPayload payload,
        IIssueGrain issueGrain,
        int issueNumber,
        CancellationToken ct)
    {
        try
        {
            await issueGrain.StartWorkAsync();
            await ReplyAsync(
                replies,
                delivery,
                projectId,
                connection,
                payload.IssueNumber,
                payload.CommentId,
                GitHubCommentKinds.CommandReplyStarted,
                GitHubIssueCommandComments.Started(projectId, issueNumber),
                ct);
        }
        catch (IssueStartBlockedException ex)
        {
            await ReplyAsync(
                replies,
                delivery,
                projectId,
                connection,
                payload.IssueNumber,
                payload.CommentId,
                GitHubCommentKinds.CommandReplyStartFailed,
                GitHubIssueCommandComments.StartFailed(ex.Message),
                ct);
        }
        catch (IssueStartRepositoryUnavailableException ex)
        {
            await ReplyAsync(
                replies,
                delivery,
                projectId,
                connection,
                payload.IssueNumber,
                payload.CommentId,
                GitHubCommentKinds.CommandReplyStartFailed,
                GitHubIssueCommandComments.StartFailed(ex.Message),
                ct);
        }
    }

    private async Task ReplyAsync(
        GitHubCommandReplyStore replies,
        GitHubCommandReplyDeliveryService delivery,
        string projectId,
        GitHubConnection connection,
        int githubIssueNumber,
        string githubCommentId,
        string replyKind,
        string body,
        CancellationToken ct)
    {
        var operationKey = GitHubCommentKinds.CommandReplyOperationKey(
            connection.Id,
            githubIssueNumber,
            githubCommentId,
            replyKind);
        var marker = GitHubCommentKinds.CommandReplyMarker(
            connection.Id,
            githubIssueNumber,
            githubCommentId,
            replyKind);
        var reply = await replies.GetOrCreateAsync(
            projectId,
            connection.Id,
            connection.RepositoryName,
            githubIssueNumber,
            githubCommentId,
            operationKey,
            marker,
            body,
            ct);
        if (!reply.IsPosted && !reply.IsFailed)
            await delivery.DeliverAsync(reply, force: true, ct: ct);
    }
}
