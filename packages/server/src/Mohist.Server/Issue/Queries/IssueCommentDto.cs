namespace Mohist.Server.Issue.Queries;

[GenerateSerializer]
public sealed record IssueCommentDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string IssueId,
    [property: Id(2)] string Body,
    [property: Id(3)] string CreatedAt);
