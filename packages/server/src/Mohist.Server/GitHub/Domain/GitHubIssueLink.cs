using System.Net;

namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubIssueLink
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    /// <summary>Zero while the mirror creation intent is still pending.</summary>
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
    public string? MirrorMarker { get; set; }
    public bool MirrorCreateAttempted { get; set; }
    public bool CommandRequested { get; set; }
    public string SyncStatus { get; set; } = GitHubSyncStatus.Healthy;
    public GitHubSyncError? LastError { get; set; }
    public IReadOnlySet<string> PostedComments { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The <c>mohist:</c> state label currently projected onto the GitHub
    /// issue, or <c>null</c> when none was set yet. The label is
    /// mutually-exclusive by construction (the port strips all other
    /// <c>mohist:</c> labels), so this single value is the persisted
    /// idempotency gate for label write-back.
    /// </summary>
    public string? StateLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsPending => GithubIssueNumber <= 0;

    public bool HasPostedComment(string key) => PostedComments.Contains(key);
}

public sealed record GitHubIssueLinkClaim(bool Won, GitHubIssueLink? Link);

public sealed record GitHubMirrorCreateReservation(GitHubIssueLink Link, bool Acquired);

public static class GitHubCommentOperationStatus
{
    public const string Reserved = "reserved";
    public const string Posted = "posted";
}

public static class GitHubRemoteOutcome
{
    public static bool IsUnknown(Exception exception) => exception switch
    {
        TaskCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => true,
        _ => false,
    };
}

public static class GitHubSyncStatus
{
    public const string Healthy = "healthy";
    public const string Error = "error";
}

public sealed record GitHubSyncError(
    string Operation,
    string Code,
    string Detail,
    DateTimeOffset OccurredAt);

/// <summary>
/// Comment kinds the comment port may post; the posted set lives on
/// <see cref="GitHubIssueLink.PostedComments"/> so redelivery never posts a
/// node comment twice. Write-back close markers live in the same set so close
/// is as idempotent as comments. Command replies use their dedicated durable
/// reply ledger and marker-based delivery worker.
/// </summary>
public static class GitHubCommentKinds
{
    public const string CommandReplyUnknownVerb = "command-reply-unknown-verb";
    public const string CommandReplyStarted = "command-reply-started";
    public const string CommandReplyAlreadyLinked = "command-reply-already-linked";
    public const string CommandReplyStartFailed = "command-reply-start-failed";

    public static string CommandReply(string commentId) => $"command-reply:{commentId}";

    public static string CommandReplyOperationKey(
        string connectionId,
        int githubIssueNumber,
        string commentId,
        string replyKind) =>
        $"command-reply:{connectionId}:{githubIssueNumber}:{commentId}:{replyKind}";

    public static string CommandReplyMarker(
        string connectionId,
        int githubIssueNumber,
        string commentId,
        string replyKind) =>
        $"<!-- mohist:command-reply:{connectionId}:{githubIssueNumber}:{commentId}:{replyKind} -->";

    public const string MirrorCreated = "writeback-mirror-created";
    public const string WorkStarted = "writeback-work-started";
    public const string ApprovalRequested = "writeback-approval-requested";
    public const string Completed = "writeback-completed";
    public const string Cancelled = "writeback-cancelled";
    public const string ClosedCompleted = "writeback-closed-completed";
    public const string ClosedNotPlanned = "writeback-closed-not-planned";
    public const string ReopenedDoneFollowUp = "writeback-reopened-done-follow-up";
    public const string Create = "create";
    public const string Content = "content";
    public const string Reconcile = "reconcile";
    public const string Link = "link";
}

/// <summary>
/// The mutually-exclusive <c>mohist:</c> state label family projected onto
/// linked GitHub issues.
/// </summary>
public static class GitHubStateLabels
{
    public const string InProgress = "mohist:in-progress";
    public const string AwaitingApproval = "mohist:awaiting-approval";
    public const string Blocked = "mohist:blocked";
    public const string Done = "mohist:done";
}

/// <summary>
/// Invisible HTML marker embedded in a Mohist-created GitHub issue body. It
/// makes an unknown create result safely reconcilable without matching on
/// mutable user content. The marker is removed before content enters Mohist.
/// </summary>
public static class GitHubMirrorMarker
{
    public static string For(string linkId) => $"<!-- mohist:mirror:{linkId} -->";

    public static string Append(string? body, string marker) =>
        string.IsNullOrEmpty(body) ? marker : $"{body}\n\n{marker}";

    public static string? Strip(string? body, string marker)
    {
        if (body is null) return null;
        var suffix = $"\n\n{marker}";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            return body[..^suffix.Length];
        if (string.Equals(body, marker, StringComparison.Ordinal))
            return string.Empty;
        return body.Replace(marker, string.Empty, StringComparison.Ordinal);
    }
}
