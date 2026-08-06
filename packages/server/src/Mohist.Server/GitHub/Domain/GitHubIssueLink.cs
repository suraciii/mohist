namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubIssueLink
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
    public IReadOnlySet<string> PostedComments { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool HasPostedComment(string key) => PostedComments.Contains(key);
}

/// <summary>
/// Comment kinds the minimal comment port may post; the posted set lives on
/// <see cref="GitHubIssueLink.PostedComments"/> so redelivery never posts a
/// node comment twice. The full write-back comment family extends this set.
/// </summary>
public static class GitHubCommentKinds
{
    public const string FeedRejected = "feed-rejected";
}
