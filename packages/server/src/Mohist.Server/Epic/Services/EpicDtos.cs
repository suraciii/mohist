using Orleans;

namespace Mohist.Server.Epic.Services;

[GenerateSerializer]
public sealed record EpicDto(
    [property: Id(0)] string Id,
    [property: Id(1)] int? Number,
    [property: Id(2)] string Title,
    [property: Id(3)] string Description,
    [property: Id(4)] string Priority,
    [property: Id(5)] string Status,
    [property: Id(6)] string CreatedAt,
    [property: Id(7)] string UpdatedAt);

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
    int? Number,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    EpicProgressDto Progress);

public sealed record LinkedIssueDto(
    string Id,
    int Number,
    string Title,
    string Status,
    string Stage,
    string Health,
    string? Priority);

public sealed record EpicDetailDto(
    string Id,
    int? Number,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<LinkedIssueDto> LinkedIssues,
    EpicProgressDto Progress);
