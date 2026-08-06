namespace Mohist.Server.Infrastructure.Data.GitHub;

/// <summary>
/// Feed idempotency record: maps one GitHub issue
/// <c>(ProjectId, RepositoryName, GithubIssueNumber)</c> to the Mohist issue
/// number created for it. The unique index on the triple is the persisted
/// gate that guarantees a GitHub issue is fed exactly once, regardless of
/// duplicate events, unlabel/re-label cycles, or dispatcher redelivery.
/// <c>PostedCommentsJson</c> holds the write-back bookkeeping (comment
/// kinds already posted) that the minimal comment port needs to stay
/// idempotent across redeliveries.
/// </summary>
public sealed class GitHubIssueLinkRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
    public string PostedCommentsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
