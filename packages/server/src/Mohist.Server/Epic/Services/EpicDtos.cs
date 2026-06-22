using Mohist.Server.Issue.Services;
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
    [property: Id(7)] string UpdatedAt,
    [property: Id(8)] string? PauseReason = null);

public sealed record EpicProgressDto(
    int DeliveredCount,
    int TotalIssueCount,
    IReadOnlyList<EpicProgressIssueDto> BlockedIssues,
    IReadOnlyList<EpicProgressIssueDto> ActiveIssues,
    EpicNextIssueDto? NextIssue,
    string? NextIssueReason,
    bool ReadyToMarkDone);

public sealed record EpicNextIssueDto(string Id, int Number, string Title);

public sealed record EpicProgressIssueDto(
    string Id,
    int Number,
    string Title,
    string Health);

public sealed record EpicWithProgressDto(
    string Id,
    int? Number,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    EpicProgressDto Progress,
    string? PauseReason = null);

public sealed record LinkedIssueDto
{
    public string Id { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Stage { get; init; } = "";
    public string Health { get; init; } = "";
    public string? Priority { get; init; }
    public bool CanStart { get; init; }
    public IssueStartBlockerDto? StartBlocker { get; init; }
    public int[] PrerequisiteNumbers { get; init; } = [];
    public IReadOnlyList<IssuePrerequisiteRefDto> ExternalPrerequisites { get; init; } = [];

    public LinkedIssueDto() { }

    public LinkedIssueDto(
        string Id,
        int Number,
        string Title,
        string Status,
        string Stage,
        string Health,
        string? Priority,
        bool CanStart = false,
        IssueStartBlockerDto? StartBlocker = null,
        int[]? PrerequisiteNumbers = null,
        IReadOnlyList<IssuePrerequisiteRefDto>? ExternalPrerequisites = null)
        : this()
    {
        this.Id = Id;
        this.Number = Number;
        this.Title = Title;
        this.Status = Status;
        this.Stage = Stage;
        this.Health = Health;
        this.Priority = Priority;
        this.CanStart = CanStart;
        this.StartBlocker = StartBlocker;
        this.PrerequisiteNumbers = PrerequisiteNumbers ?? [];
        this.ExternalPrerequisites = ExternalPrerequisites ?? [];
    }
}

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
    EpicProgressDto Progress,
    string? PauseReason = null);
