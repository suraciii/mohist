using Orleans;

namespace Mohist.Server.Epics;

[GenerateSerializer]
public sealed record EpicDto(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string Priority,
    [property: Id(4)] string Status,
    [property: Id(5)] string CreatedAt,
    [property: Id(6)] string UpdatedAt);

public sealed record EpicProgressDto(
    int CompletedCount,
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
