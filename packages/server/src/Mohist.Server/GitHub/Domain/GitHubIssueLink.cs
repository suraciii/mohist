namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubIssueLink
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
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

    public bool HasPostedComment(string key) => PostedComments.Contains(key);
}

/// <summary>
/// Comment kinds the comment port may post; the posted set lives on
/// <see cref="GitHubIssueLink.PostedComments"/> so redelivery never posts a
/// node comment twice. Write-back close markers live in the same set so
/// close is as idempotent as comments.
/// </summary>
public static class GitHubCommentKinds
{
    public const string FeedRejected = "feed-rejected";
    public const string WorkStarted = "writeback-work-started";
    public const string ApprovalRequested = "writeback-approval-requested";
    public const string Completed = "writeback-completed";
    public const string Cancelled = "writeback-cancelled";
    public const string ClosedCompleted = "writeback-closed-completed";
    public const string ClosedNotPlanned = "writeback-closed-not-planned";
}

/// <summary>
/// The mutually-exclusive <c>mohist:</c> state label family projected onto
/// fed GitHub issues. The intake label must not use this prefix (enforced
/// by <see cref="GitHubConnection.Validate"/>).
/// </summary>
public static class GitHubStateLabels
{
    public const string InProgress = "mohist:in-progress";
    public const string AwaitingApproval = "mohist:awaiting-approval";
    public const string Blocked = "mohist:blocked";
    public const string Done = "mohist:done";
}
