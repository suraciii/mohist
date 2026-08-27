namespace Mohist.Server.GitHub.Domain;

public sealed class GitHubCommandReply
{
    public string Id { get; init; } = string.Empty;
    public string ProjectId { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public int GithubIssueNumber { get; init; }
    public string GithubCommentId { get; init; } = string.Empty;
    public string Marker { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public DateTimeOffset? PostedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public bool IsPosted => PostedAt is not null;
}
