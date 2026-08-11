using Mohist.Server.Issue.Services;
using Orleans;

namespace Mohist.Server.Epic.Services;

[GenerateSerializer]
public sealed record EpicDto(
    [property: Id(0)] string ProjectId,
    [property: Id(1)] int Number,
    [property: Id(2)] string Title,
    [property: Id(3)] string Description,
    [property: Id(4)] string Priority,
    [property: Id(5)] string Status,
    [property: Id(6)] string CreatedAt,
    [property: Id(7)] string UpdatedAt,
    [property: Id(8)] string? PauseReason = null);

/// <summary>
/// Resolved issue number passed from the HTTP layer to the grain's batch
/// link/unlink entry points. The grain receives the number with its wire
/// identifier so duplicate requests are deterministic.
/// </summary>
[GenerateSerializer]
public sealed record BatchMembershipRequestItem(
    [property: Id(0)] string Identifier,
    [property: Id(1)] int IssueNumber);

public sealed record EpicProgressDto(
    int DeliveredCount,
    int TotalIssueCount,
    IReadOnlyList<EpicProgressIssueDto> BlockedIssues,
    IReadOnlyList<EpicProgressIssueDto> ActiveIssues,
    EpicNextIssueDto? NextIssue,
    string? NextIssueReason,
    bool ReadyToMarkDone);

public sealed record EpicNextIssueDto(int Number, string Title);

public sealed record EpicProgressIssueDto(
    int Number,
    string Title,
    string Health);

public sealed record EpicWithProgressDto(
    string ProjectId,
    int Number,
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
    string ProjectId,
    int Number,
    string Title,
    string Description,
    string Priority,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    IReadOnlyList<LinkedIssueDto> LinkedIssues,
    EpicProgressDto Progress,
    string? PauseReason = null)
{
    public int? NextIssueNumber => Progress.NextIssue?.Number;
    public string? NextIssueReason => Progress.NextIssueReason;
};

/// <summary>
/// Per-issue result emitted by <c>IEpicGrain.LinkIssuesAsync</c> /
/// <c>UnlinkIssuesAsync</c>. The HTTP layer wraps the list in
/// <c>{ results: [...] }</c>. The <see cref="Status"/> discriminator is
/// machine-friendly; <see cref="OwningEpicNumber"/> / <see cref="OwningEpicTitle"/>
/// are populated for outcomes that retain or establish an Epic relationship.
/// </summary>
[GenerateSerializer]
public sealed record BatchMembershipOutcome(
    [property: Id(0)] string Identifier,
    [property: Id(1)] string Status,
    [property: Id(2)] int? IssueNumber = null,
    [property: Id(3)] int? OwningEpicNumber = null,
    [property: Id(4)] string? OwningEpicTitle = null)
{
    public static BatchMembershipOutcome Linked(
        string identifier, int issueNumber, int owningEpicNumber, string owningEpicTitle) =>
        new(identifier, "linked", issueNumber, owningEpicNumber, owningEpicTitle);

    public static BatchMembershipOutcome AlreadyLinked(
        string identifier, int issueNumber, int owningEpicNumber, string owningEpicTitle) =>
        new(identifier, "already-linked", issueNumber, owningEpicNumber, owningEpicTitle);

    public static BatchMembershipOutcome Unlinked(string identifier, int issueNumber) =>
        new(identifier, "unlinked", issueNumber);

    public static BatchMembershipOutcome WasNotAMember(string identifier, int issueNumber) =>
        new(identifier, "was-not-a-member", issueNumber);

    public static BatchMembershipOutcome Conflict(
        string identifier, int issueNumber, int owningEpicNumber, string owningEpicTitle) =>
        new(identifier, "conflict", issueNumber, owningEpicNumber, owningEpicTitle);

    public static BatchMembershipOutcome NotFound(string identifier) =>
        new(identifier, "not-found");
}
