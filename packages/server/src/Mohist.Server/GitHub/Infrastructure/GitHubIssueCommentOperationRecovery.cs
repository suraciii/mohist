using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mohist.Server.GitHub.Domain;
using Mohist.Server.GitHub.Ports;
using Mohist.Server.Infrastructure;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;

namespace Mohist.Server.GitHub.Infrastructure;

/// <summary>
/// Recovers durable comment-like write-back reservations after a process
/// crash or an unknown GitHub response. Comments are reconciled by their
/// invisible marker; closes are reconciled by the remote issue state. A
/// marker with zero matches may be posted again, one match is already done,
/// and multiple matches are terminally ambiguous.
/// </summary>
public sealed class GitHubIssueCommentOperationRecoveryService : IScopedService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private readonly GitHubIssueLinkStore _links;
    private readonly GitHubConnectionStore _connections;
    private readonly IGitHubCommentPort _comments;
    private readonly IGitHubIssuePort _issues;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubIssueCommentOperationRecoveryService> _log;

    public GitHubIssueCommentOperationRecoveryService(
        GitHubIssueLinkStore links,
        GitHubConnectionStore connections,
        IGitHubCommentPort comments,
        IGitHubIssuePort issues,
        TimeProvider timeProvider,
        ILogger<GitHubIssueCommentOperationRecoveryService> log)
    {
        _links = links;
        _connections = connections;
        _comments = comments;
        _issues = issues;
        _timeProvider = timeProvider;
        _log = log;
    }

    public async Task<int> ProcessPendingAsync(
        int batchSize = 100,
        CancellationToken ct = default)
    {
        var pending = await _links.ListPendingCommentOperationsAsync(batchSize, ct);
        var processed = 0;
        foreach (var operation in pending)
        {
            ct.ThrowIfCancellationRequested();
            // The store claim includes the Active connection predicate. Do not
            // split this boundary into a status read followed by an
            // unconditional lease update: Disable may win between those two
            // operations.
            var claimed = await _links.TryClaimCommentOperationAsync(operation.Id, LeaseDuration, ct);
            if (claimed is null)
                continue;

            try
            {
                await RecoverAsync(claimed, ct);
                processed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(claimed, ex, ct);
            }
        }
        return processed;
    }

    private async Task RecoverAsync(
        GitHubIssueCommentOperation operation,
        CancellationToken ct)
    {
        var link = await _links.GetByIdAsync(operation.LinkId, ct)
            ?? throw new GitHubSynchronizationException("github_link_missing", "GitHub link disappeared while recovering an operation");
        if (operation.GithubIssueNumber <= 0
            || link.GithubIssueNumber != operation.GithubIssueNumber)
            return;

        var connection = await _connections.GetByRepositoryAsync(link.ProjectId, link.RepositoryName, ct)
            ?? throw new GitHubSynchronizationException("github_connection_missing", "GitHub connection disappeared while recovering an operation");
        if (connection.Status != GitHubConnectionStatus.Active)
            throw new GitHubSynchronizationException("github_connection_disabled", "GitHub connection is disabled");

        switch (operation.Kind)
        {
            case GitHubCommentOperationKind.Comment:
                await RecoverCommentAsync(operation, connection, link, ct);
                return;
            case GitHubCommentOperationKind.Close:
                await RecoverCloseAsync(operation, connection, link, ct);
                return;
            default:
                await MarkAmbiguousAsync(operation, link,
                    $"unknown GitHub operation kind '{operation.Kind}'", ct);
                return;
        }
    }

    private async Task RecoverCommentAsync(
        GitHubIssueCommentOperation operation,
        GitHubConnection connection,
        GitHubIssueLink link,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.Marker) || operation.Body is null)
        {
            await MarkAmbiguousAsync(operation, link,
                "GitHub comment reservation has no durable body marker", ct);
            return;
        }

        IReadOnlyList<string> matches;
        try
        {
            matches = await _comments.FindCommentIdsByMarkerAsync(
                connection,
                link.GithubIssueNumber,
                operation.Marker,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new GitHubRemoteOutcomeUnknownException(
                "GitHub comment marker lookup did not return a trustworthy result",
                ex);
        }
        var distinctMatches = matches
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctMatches.Length > 1)
        {
            await MarkAmbiguousAsync(operation, link,
                "GitHub comment marker matched multiple comments", ct);
            return;
        }

        if (distinctMatches.Length == 1)
        {
            await _links.MarkCommentPostedAsync(
                link.Id,
                operation.CommentKey,
                operation.GithubIssueNumber,
                ct);
            return;
        }

        await _comments.PostCommentAsync(
            connection,
            link.GithubIssueNumber,
            GitHubMirrorMarker.Append(operation.Body, operation.Marker),
            ct);
        await _links.MarkCommentPostedAsync(
            link.Id,
            operation.CommentKey,
            operation.GithubIssueNumber,
            ct);
    }

    private async Task RecoverCloseAsync(
        GitHubIssueCommentOperation operation,
        GitHubConnection connection,
        GitHubIssueLink link,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.StateReason))
        {
            await MarkAmbiguousAsync(operation, link,
                "GitHub close reservation has no durable state reason", ct);
            return;
        }

        GitHubIssueSnapshot? snapshot;
        try
        {
            snapshot = await _issues.GetIssueAsync(connection, link.GithubIssueNumber, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new GitHubRemoteOutcomeUnknownException(
                "GitHub close reconciliation did not return a trustworthy issue state",
                ex);
        }
        if (snapshot is null)
            throw new GitHubRemoteOutcomeUnknownException(
                "GitHub close reconciliation returned no issue state");
        if (snapshot.Number != link.GithubIssueNumber
            || string.IsNullOrWhiteSpace(snapshot.State))
        {
            throw new GitHubRemoteOutcomeUnknownException(
                "GitHub close reconciliation returned an incomplete issue state");
        }

        if (string.Equals(snapshot.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(snapshot.StateReason, operation.StateReason, StringComparison.OrdinalIgnoreCase))
            {
                await _links.MarkCommentPostedAsync(
                    link.Id,
                    operation.CommentKey,
                    operation.GithubIssueNumber,
                    ct);
                return;
            }

            await MarkAmbiguousAsync(operation, link,
                $"GitHub issue is already closed with state reason '{snapshot.StateReason ?? "unknown"}'", ct);
            return;
        }

        if (!string.Equals(snapshot.State, "open", StringComparison.OrdinalIgnoreCase))
            throw new GitHubRemoteOutcomeUnknownException(
                $"GitHub close reconciliation returned unsupported state '{snapshot.State}'");

        await _comments.CloseIssueAsync(
            connection,
            link.GithubIssueNumber,
            operation.StateReason,
            ct);
        await _links.MarkCommentPostedAsync(
            link.Id,
            operation.CommentKey,
            operation.GithubIssueNumber,
            ct);
    }

    private async Task HandleFailureAsync(
        GitHubIssueCommentOperation operation,
        Exception exception,
        CancellationToken ct)
    {
        var detail = exception.Message.Length <= 1000
            ? exception.Message
            : exception.Message[..1000];
        if (string.IsNullOrWhiteSpace(detail))
            detail = exception.GetType().Name;

        if (exception is GitHubSynchronizationException { Code: "github_connection_disabled" })
        {
            await _links.ReleaseCommentOperationLeaseAsync(operation.Id, ct);
            return;
        }

        if (!GitHubRemoteOutcome.IsUnknown(exception))
            await _links.DeleteCommentOperationAsync(operation.Id, ct);
        else
            await _links.DeferCommentOperationAsync(operation.Id, detail, ct);

        var link = await _links.GetByIdAsync(operation.LinkId, ct);
        if (link is null)
            return;
        await _links.MarkErrorAsync(
            link.Id,
            new GitHubSyncError(
                operation.Kind == GitHubCommentOperationKind.Close
                    ? GitHubWriteBackOperation.Close
                    : GitHubWriteBackOperation.Comment,
                exception is HttpRequestException { StatusCode: { } status }
                    ? ((int)status).ToString()
                    : exception.GetType().Name,
                detail,
                _timeProvider.GetUtcNow()),
            expectedGithubIssueNumber: operation.GithubIssueNumber > 0
                ? operation.GithubIssueNumber
                : null,
            ct: ct);
        _log.LogWarning(
            exception,
            "GitHub operation {OperationId} recovery failed; reservation remains recoverable",
            operation.Id);
    }

    private async Task MarkAmbiguousAsync(
        GitHubIssueCommentOperation operation,
        GitHubIssueLink link,
        string detail,
        CancellationToken ct)
    {
        await _links.MarkCommentOperationAmbiguousAsync(operation.Id, detail, ct);
        await _links.MarkErrorAsync(
            link.Id,
            new GitHubSyncError(
                operation.Kind == GitHubCommentOperationKind.Close
                    ? GitHubWriteBackOperation.Close
                    : GitHubWriteBackOperation.Comment,
                "ambiguous",
                detail,
                _timeProvider.GetUtcNow()),
            expectedGithubIssueNumber: operation.GithubIssueNumber > 0
                ? operation.GithubIssueNumber
                : null,
            ct: ct);
        _log.LogError(
            "GitHub operation {OperationId} is ambiguous and will not be posted again: {Detail}",
            operation.Id,
            detail);
    }
}

/// <summary>
/// Hosted safety-net consumer for reserved GitHub comment operations. It
/// scans durable rows after startup and between bounded wake intervals; the
/// row lease prevents it from racing an in-flight request.
/// </summary>
public sealed class GitHubIssueCommentOperationRecoveryOptions
{
    public bool HostedWorkerEnabled { get; set; } = true;
}

public sealed class GitHubIssueCommentOperationRecoveryWorker : BackgroundService
{
    private static readonly TimeSpan SafetyPollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<GitHubIssueCommentOperationRecoveryOptions> _options;
    private readonly ILogger<GitHubIssueCommentOperationRecoveryWorker> _log;

    public GitHubIssueCommentOperationRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<GitHubIssueCommentOperationRecoveryOptions> options,
        ILogger<GitHubIssueCommentOperationRecoveryWorker> log)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<GitHubIssueCommentOperationRecoveryService>()
                .ProcessPendingAsync(ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GitHub comment operation recovery pass failed");
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.HostedWorkerEnabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessPendingAsync(stoppingToken);
            try
            {
                await Task.Delay(SafetyPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
