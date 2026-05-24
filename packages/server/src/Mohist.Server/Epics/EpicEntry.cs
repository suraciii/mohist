namespace Mohist.Server.Epics;

public class EpicEntry
{
    public string Id { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Description { get; set; } = "";
    public string Priority { get; set; } = "p2";
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EpicIssueEntry
{
    public string EpicId { get; set; } = null!;
    public string ProjectId { get; set; } = null!;
    public string IssueId { get; set; } = null!;
    public int IssueNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record EpicDto(string Id, string Title, string Description, string Priority, string Status, string CreatedAt, string UpdatedAt);

public sealed record EpicProgressDto(
    int DeliveredCount,
    int TotalIssueCount,
    IReadOnlyList<string> BlockedIssues,
    IReadOnlyList<string> ActiveIssues,
    EpicNextIssueDto? NextIssue,
    bool ReadyToMarkDone);

public sealed record EpicNextIssueDto(string Id, int Number, string Title);

public sealed record EpicWithProgressDto(
    string Id,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    EpicProgressDto Progress);

public sealed record LinkedIssueDto(string Id, int Number, string Title, string Status, string Stage, string? Priority);

public sealed record EpicDetailDto(
    string Id,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<LinkedIssueDto> LinkedIssues,
    EpicProgressDto Progress);
