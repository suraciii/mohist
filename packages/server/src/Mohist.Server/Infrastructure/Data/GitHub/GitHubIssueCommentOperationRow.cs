namespace Mohist.Server.Infrastructure.Data.GitHub;

/// <summary>
/// Durable reservation for one outbound GitHub comment-like operation. The
/// unique link/key pair is claimed before the remote request so concurrent
/// delivery cannot post the same operation twice. A reserved row remains
/// reserved when the request outcome is unknown; a known rejected request can
/// release it for a later retry.
/// </summary>
public sealed class GitHubIssueCommentOperationRow
{
    public required string Id { get; set; }
    public required string LinkId { get; set; }
    public int GithubIssueNumber { get; set; }
    public required string CommentKey { get; set; }
    public string? Kind { get; set; }
    public string? Body { get; set; }
    public string? StateReason { get; set; }
    public string? Marker { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}
