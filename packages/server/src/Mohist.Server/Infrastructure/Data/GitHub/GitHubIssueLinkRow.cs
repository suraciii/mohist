namespace Mohist.Server.Infrastructure.Data.GitHub;

/// <summary>
/// GitHub link idempotency record: maps one GitHub issue
/// <c>(ProjectId, RepositoryName, GithubIssueNumber)</c> to the Mohist issue
/// number associated with it. The unique index on the triple is the persisted
/// gate that makes mirror and command redelivery converge on one link, even
/// across duplicate events and dispatcher redelivery.
/// <c>PostedCommentsJson</c> holds the write-back bookkeeping (delivered
/// comment kinds) that the minimal comment port needs to stay idempotent
/// across redeliveries.
/// </summary>
public sealed class GitHubIssueLinkRow
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int GithubIssueNumber { get; set; }
    public int IssueNumber { get; set; }
    public string? MirrorMarker { get; set; }
    public bool MirrorCreateAttempted { get; set; }
    public bool CommandRequested { get; set; }
    public string PostedCommentsJson { get; set; } = "[]";
    public string? StateLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
