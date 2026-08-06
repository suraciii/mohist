using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Events;

namespace Mohist.Server.GitHub.Subscriptions;

/// <summary>
/// Progress write-back translator: projects Mohist pipeline state onto the
/// GitHub issue a link points at. Five internal events drive the five
/// states of the <c>mohist:</c> label family:
/// <list type="bullet">
/// <item><c>issue.work-started</c> — <c>mohist:in-progress</c> + comment</item>
/// <item><c>workflow.stage.approval-requested</c> — <c>mohist:awaiting-approval</c> + comment</item>
/// <item><c>workflow.run.failed</c> — <c>mohist:blocked</c></item>
/// <item><c>issue.completed</c> — comment + <c>mohist:done</c> + close (completed)</item>
/// <item><c>issue.cancelled</c> — cancel comment + close (not_planned)</item>
/// </list>
/// <para>
/// Write-back is best-effort by contract: every GitHub-side operation is
/// gated by the link's persisted bookkeeping (<see cref="GitHubIssueLink.PostedComments"/>
/// and <see cref="GitHubIssueLink.StateLabel"/>), fails independently, and
/// a failure never blocks the pipeline — it lands in
/// <see cref="GitHubWriteBackFailure"/> and, for 401/403, flags the
/// connection <see cref="GitHubConnection.NeedsAttention"/>. Redelivery
/// retries only the operations that did not succeed yet.
/// </para>
/// </summary>
[Subscription(
    Type = "com.mohist.issue.work-started|com.mohist.workflow.stage.approval-requested|com.mohist.workflow.run.failed|com.mohist.issue.completed|com.mohist.issue.cancelled",
    Identity = "Mohist.Server.GitHub.Subscriptions.GitHubWriteBackHandler")]
public sealed class GitHubWriteBackHandler : ICloudEventHandler
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.Ordinal)
    {
        EventCatalog.ReverseDns.IssueWorkStarted,
        EventCatalog.ReverseDns.StageApprovalRequested,
        EventCatalog.ReverseDns.WorkflowRunFailed,
        EventCatalog.ReverseDns.IssueCompleted,
        EventCatalog.ReverseDns.IssueCancelled,
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<GitHubWriteBackHandler> _log;

    public GitHubWriteBackHandler(
        IServiceScopeFactory scopes,
        ILogger<GitHubWriteBackHandler> log)
    {
        _scopes = scopes;
        _log = log;
    }

    public bool Filter(CloudEvent evt) =>
        evt is not null && SupportedTypes.Contains(evt.Type);

    public Task HandleAsync(CloudEvent evt, CancellationToken ct) => WriteBackAsync(evt, ct);

    private async Task WriteBackAsync(CloudEvent evt, CancellationToken ct)
    {
        if (!CloudEventLineage.TryReadIssueContext(evt, out var context))
            return;

        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var link = await sp.GetRequiredService<GitHubIssueLinkStore>()
            .GetByIssueAsync(context.ProjectId, context.IssueNumber, ct);
        if (link is null)
            return;
        var connection = await sp.GetRequiredService<GitHubConnectionStore>()
            .GetByRepositoryAsync(context.ProjectId, link.RepositoryName, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;

        switch (evt.Type)
        {
            case EventCatalog.ReverseDns.IssueWorkStarted:
                await SetStateLabelAsync(sp, connection, link, GitHubStateLabels.InProgress, evt.Type, ct);
                await PostCommentAsync(sp, connection, link, GitHubCommentKinds.WorkStarted,
                    GitHubWriteBackComments.WorkStarted(link.IssueNumber), evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.StageApprovalRequested:
                await SetStateLabelAsync(sp, connection, link, GitHubStateLabels.AwaitingApproval, evt.Type, ct);
                await PostCommentAsync(sp, connection, link, GitHubCommentKinds.ApprovalRequested,
                    GitHubWriteBackComments.ApprovalRequested(link.IssueNumber), evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.WorkflowRunFailed:
                await SetStateLabelAsync(sp, connection, link, GitHubStateLabels.Blocked, evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.IssueCompleted:
                await PostCommentAsync(sp, connection, link, GitHubCommentKinds.Completed,
                    GitHubWriteBackComments.Completed(link.IssueNumber), evt.Type, ct);
                await SetStateLabelAsync(sp, connection, link, GitHubStateLabels.Done, evt.Type, ct);
                await CloseAsync(sp, connection, link, "completed", GitHubCommentKinds.ClosedCompleted, evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.IssueCancelled:
                await PostCommentAsync(sp, connection, link, GitHubCommentKinds.Cancelled,
                    GitHubWriteBackComments.Cancelled(link.IssueNumber), evt.Type, ct);
                await CloseAsync(sp, connection, link, "not_planned", GitHubCommentKinds.ClosedNotPlanned, evt.Type, ct);
                break;
        }
    }

    private async Task SetStateLabelAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        string stateLabel,
        string eventType,
        CancellationToken ct)
    {
        if (string.Equals(link.StateLabel, stateLabel, StringComparison.Ordinal))
            return;
        try
        {
            await sp.GetRequiredService<IGitHubCommentPort>()
                .ReplaceStateLabelAsync(connection, link.GithubIssueNumber, stateLabel, ct);
            await sp.GetRequiredService<GitHubIssueLinkStore>()
                .SetStateLabelAsync(link.Id, stateLabel, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.Label, ex, ct);
        }
    }

    private async Task PostCommentAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        string commentKey,
        string body,
        string eventType,
        CancellationToken ct)
    {
        if (link.HasPostedComment(commentKey))
            return;
        try
        {
            await sp.GetRequiredService<IGitHubCommentPort>()
                .PostCommentAsync(connection, link.GithubIssueNumber, body, ct);
            await sp.GetRequiredService<GitHubIssueLinkStore>()
                .MarkCommentPostedAsync(link.Id, commentKey, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.Comment, ex, ct);
        }
    }

    private async Task CloseAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        string stateReason,
        string closeKey,
        string eventType,
        CancellationToken ct)
    {
        if (link.HasPostedComment(closeKey))
            return;
        try
        {
            await sp.GetRequiredService<IGitHubCommentPort>()
                .CloseIssueAsync(connection, link.GithubIssueNumber, stateReason, ct);
            await sp.GetRequiredService<GitHubIssueLinkStore>()
                .MarkCommentPostedAsync(link.Id, closeKey, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.Close, ex, ct);
        }
    }

    private async Task RecordFailureAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        string eventType,
        string operation,
        Exception ex,
        CancellationToken ct)
    {
        _log.LogWarning(ex,
            "GitHub write-back {Operation} for connection {ConnectionId} github issue #{GithubIssueNumber} failed",
            operation, connection.Id, link.GithubIssueNumber);
        try
        {
            var failure = new GitHubWriteBackFailure
            {
                ProjectId = connection.ProjectId,
                ConnectionId = connection.Id,
                RepositoryName = connection.RepositoryName,
                GithubIssueNumber = link.GithubIssueNumber,
                IssueNumber = link.IssueNumber,
                EventType = eventType,
                Operation = operation,
                ErrorCode = ex is HttpRequestException { StatusCode: { } status }
                    ? ((int)status).ToString()
                    : ex.GetType().Name,
                ErrorDetail = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000],
            };
            await sp.GetRequiredService<GitHubWriteBackFailureStore>().CreateAsync(failure, ct);
            if (IsCredentialFailure(ex))
            {
                await sp.GetRequiredService<GitHubConnectionStore>()
                    .MarkNeedsAttentionAsync(connection.ProjectId, connection.Id, needsAttention: true, ct);
            }
        }
        catch (Exception recordEx) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(recordEx,
                "GitHub write-back failure record for connection {ConnectionId} could not be persisted",
                connection.Id);
        }
    }

    private static bool IsCredentialFailure(Exception ex) =>
        ex is HttpRequestException
        {
            StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
        };
}
