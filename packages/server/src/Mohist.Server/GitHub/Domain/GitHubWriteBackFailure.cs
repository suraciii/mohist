namespace Mohist.Server.GitHub.Domain;

/// <summary>
/// A failed GitHub progress write-back operation, as recorded by
/// <see cref="Mohist.Server.GitHub.Infrastructure.GitHubWriteBackFailureStore"/>.
/// Write-back is best-effort by contract; the row keeps failures visible
/// and auditable without blocking the pipeline.
/// </summary>
public sealed class GitHubWriteBackFailure
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

/// <summary>
/// The single GitHub-side operation a write-back failure row describes;
/// each event may drive several independent operations (comment, label,
/// close), and each fails on its own.
/// </summary>
public static class GitHubWriteBackOperation
{
    public const string Comment = "comment";
    public const string Label = "label";
    public const string Close = "close";
}
