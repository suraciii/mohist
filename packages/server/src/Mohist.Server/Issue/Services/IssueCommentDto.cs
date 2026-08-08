namespace Mohist.Server.Issue.Services;

[GenerateSerializer]
public sealed record IssueCommentDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string ProjectId,
    [property: Id(2)] int IssueNumber,
    [property: Id(3)] string Body,
    [property: Id(4)] string CreatedAt,
    [property: Id(5)] AttachmentInfo[] Attachments,
    [property: Id(6)] string? Author,
    [property: Id(7)] string? DisplayName);
