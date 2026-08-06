namespace Mohist.Server.Infrastructure.Data.GitHub;

/// <summary>
/// One failed GitHub progress write-back operation. Write-back is
/// best-effort by contract (it never blocks the pipeline), so failures are
/// recorded here to stay visible and auditable instead of retried forever.
/// </summary>
public sealed class GitHubWriteBackFailureRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string ConnectionId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string ErrorDetail { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
