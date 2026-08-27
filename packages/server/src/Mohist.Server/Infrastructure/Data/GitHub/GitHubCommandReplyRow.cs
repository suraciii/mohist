namespace Mohist.Server.Infrastructure.Data.GitHub;

public sealed class GitHubCommandReplyRow
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public required string ConnectionId { get; set; }
    public required string RepositoryName { get; set; }
    public required int GithubIssueNumber { get; set; }
    public required string GithubCommentId { get; set; }
    public required string Marker { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset? PostedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
