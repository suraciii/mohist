namespace Mohist.Server.Issue.Domain;

public class IssueCommentEntry
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[GenerateSerializer]
public sealed record IssueCommentDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string IssueId,
    [property: Id(2)] string Body,
    [property: Id(3)] string CreatedAt);
