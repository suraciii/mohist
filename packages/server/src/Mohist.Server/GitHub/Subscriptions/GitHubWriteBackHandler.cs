using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Infrastructure;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Domain.Events;

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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubWriteBackHandler> _log;

    public GitHubWriteBackHandler(
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        ILogger<GitHubWriteBackHandler> log)
    {
        _scopes = scopes;
        _timeProvider = timeProvider;
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
        if (link.IsPending)
        {
            _log.LogDebug(
                "GitHub write-back skipped for Mohist issue #{IssueNumber}: mirror link {LinkId} is still pending",
                link.IssueNumber, link.Id);
            return;
        }
        var connections = sp.GetRequiredService<GitHubConnectionStore>();
        var connection = await connections
            .GetByRepositoryAsync(context.ProjectId, link.RepositoryName, ct);
        if (connection is null || connection.Status != GitHubConnectionStatus.Active)
            return;
        var gate = sp.GetRequiredService<GitHubConnectionGate>();

        switch (evt.Type)
        {
            case EventCatalog.ReverseDns.IssueWorkStarted:
                await SetStateLabelAsync(sp, gate, connections, connection, link, GitHubStateLabels.InProgress, evt.Type, ct);
                await PostCommentAsync(sp, gate, connections, connection, link, GitHubCommentKinds.WorkStarted,
                    GitHubWriteBackComments.WorkStarted(link.IssueNumber), evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.StageApprovalRequested:
                await SetStateLabelAsync(sp, gate, connections, connection, link, GitHubStateLabels.AwaitingApproval, evt.Type, ct);
                await PostCommentAsync(sp, gate, connections, connection, link, GitHubCommentKinds.ApprovalRequested,
                    GitHubWriteBackComments.ApprovalRequested(link.IssueNumber), evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.WorkflowRunFailed:
                await SetStateLabelAsync(sp, gate, connections, connection, link, GitHubStateLabels.Blocked, evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.IssueCompleted:
                var prUrl = await FindDeliveryPullRequestUrlAsync(sp, connection, link, evt.Type, ct);
                await PostCommentAsync(sp, gate, connections, connection, link, GitHubCommentKinds.Completed,
                    GitHubWriteBackComments.Completed(link.IssueNumber, prUrl), evt.Type, ct);
                await SetStateLabelAsync(sp, gate, connections, connection, link, GitHubStateLabels.Done, evt.Type, ct);
                await CloseAsync(sp, gate, connections, connection, link, "completed", GitHubCommentKinds.ClosedCompleted, evt.Type, ct);
                break;
            case EventCatalog.ReverseDns.IssueCancelled:
                await PostCommentAsync(sp, gate, connections, connection, link, GitHubCommentKinds.Cancelled,
                    GitHubWriteBackComments.Cancelled(link.IssueNumber, ReadCancelledReason(evt)), evt.Type, ct);
                await CloseAsync(sp, gate, connections, connection, link, "not_planned", GitHubCommentKinds.ClosedNotPlanned, evt.Type, ct);
                break;
        }
    }

    private static string? ReadCancelledReason(CloudEvent evt) =>
        evt.Data is { } data
            ? data.Deserialize<IssueCancelled>(CloudEvent.JsonOptions)?.Reason
            : null;

    private async Task<string?> FindDeliveryPullRequestUrlAsync(
        IServiceProvider sp,
        GitHubConnection connection,
        GitHubIssueLink link,
        string eventType,
        CancellationToken ct)
    {
        try
        {
            return await sp.GetRequiredService<IGitHubCommentPort>()
                .FindDeliveryPullRequestUrlAsync(connection, link.IssueNumber, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.DeliveryPullRequest, ex, ct);
            return null;
        }
    }

    private async Task SetStateLabelAsync(
        IServiceProvider sp,
        GitHubConnectionGate gate,
        GitHubConnectionStore connections,
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
            // Same send fence as comments and closes: the label write is
            // idempotent, so a Disable that wins the gate stops this
            // projection and the next event or enable reprojection re-projects
            // the current label.
            await gate.EnterAsync(connection.Id, async token =>
            {
                var current = await connections.GetByIdAsync(connection.Id, token);
                if (current is null || current.Status != GitHubConnectionStatus.Active)
                    throw new GitHubSynchronizationException(
                        "github_connection_disabled",
                        "GitHub connection is disabled");
                await sp.GetRequiredService<IGitHubCommentPort>()
                    .ReplaceStateLabelAsync(current, link.GithubIssueNumber, stateLabel, token);
                await sp.GetRequiredService<GitHubIssueLinkStore>()
                    .SetStateLabelAsync(link.Id, stateLabel, token);
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.Label, ex, ct);
        }
    }

    private async Task PostCommentAsync(
        IServiceProvider sp,
        GitHubConnectionGate gate,
        GitHubConnectionStore connections,
        GitHubConnection connection,
        GitHubIssueLink link,
        string commentKey,
        string body,
        string eventType,
        CancellationToken ct)
    {
        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        var marker = GitHubCommentOperationMarker.For(link.Id, commentKey);
        if (!await links.TryReserveCommentAsync(
            link.Id,
            commentKey,
            GitHubCommentOperationKind.Comment,
            body,
            stateReason: null,
            ct: ct))
            return;
        try
        {
            // The gate re-reads Active before the port call. A Disable that
            // commits after this event was accepted cannot be followed by
            // this send; the reservation stays pending for recovery after
            // enable. A send that won the gate settles inside it, so a
            // waiting Disable commits only after the posted bookkeeping.
            var posted = await gate.EnterAsync(connection.Id, async token =>
            {
                var current = await connections.GetByIdAsync(connection.Id, token);
                if (current is null || current.Status != GitHubConnectionStatus.Active)
                    return false;
                await sp.GetRequiredService<IGitHubCommentPort>()
                    .PostCommentAsync(
                        current,
                        link.GithubIssueNumber,
                        GitHubMirrorMarker.Append(body, marker),
                        token);
                await links.MarkCommentPostedAsync(link.Id, commentKey, link.GithubIssueNumber, token);
                return true;
            }, ct);
            if (!posted)
            {
                // Disabled won the gate: clear the reserve-time lease so the
                // reservation is claimable as soon as the connection is
                // re-enabled, instead of waiting out the lease.
                await links.ReleaseCommentOperationLeaseAsync(link.Id, commentKey, ct);
                return;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (GitHubRemoteOutcome.IsUnknown(ex))
                await links.DeferCommentOperationAsync(link.Id, commentKey, ex.Message, ct);
            else
                await links.ReleaseCommentReservationAsync(link.Id, commentKey, ct);
            await RecordFailureAsync(sp, connection, link, eventType, GitHubWriteBackOperation.Comment, ex, ct);
        }
    }

    private async Task CloseAsync(
        IServiceProvider sp,
        GitHubConnectionGate gate,
        GitHubConnectionStore connections,
        GitHubConnection connection,
        GitHubIssueLink link,
        string stateReason,
        string closeKey,
        string eventType,
        CancellationToken ct)
    {
        var links = sp.GetRequiredService<GitHubIssueLinkStore>();
        if (!await links.TryReserveCommentAsync(
            link.Id,
            closeKey,
            GitHubCommentOperationKind.Close,
            body: null,
            stateReason: stateReason,
            ct: ct))
            return;
        try
        {
            var closed = await gate.EnterAsync(connection.Id, async token =>
            {
                var current = await connections.GetByIdAsync(connection.Id, token);
                if (current is null || current.Status != GitHubConnectionStatus.Active)
                    return false;
                await sp.GetRequiredService<IGitHubCommentPort>()
                    .CloseIssueAsync(current, link.GithubIssueNumber, stateReason, token);
                await links.MarkCommentPostedAsync(link.Id, closeKey, link.GithubIssueNumber, token);
                return true;
            }, ct);
            if (!closed)
            {
                await links.ReleaseCommentOperationLeaseAsync(link.Id, closeKey, ct);
                return;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (GitHubRemoteOutcome.IsUnknown(ex))
                await links.DeferCommentOperationAsync(link.Id, closeKey, ex.Message, ct);
            else
                await links.ReleaseCommentReservationAsync(link.Id, closeKey, ct);
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
            failure.CreatedAt = _timeProvider.GetUtcNow();
            await sp.GetRequiredService<GitHubWriteBackFailureStore>().CreateAsync(failure, ct);
            await sp.GetRequiredService<GitHubIssueLinkStore>().MarkErrorAsync(
                link.Id,
                new GitHubSyncError(failure.Operation, failure.ErrorCode, failure.ErrorDetail, failure.CreatedAt),
                expectedGithubIssueNumber: link.GithubIssueNumber > 0
                    ? link.GithubIssueNumber
                    : null,
                ct: ct);
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
        }
        && !GitHubRemoteOutcome.IsRateLimited(ex);
}
