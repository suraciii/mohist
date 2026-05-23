namespace Mohist.Server.Issue.Domain;

public enum IssueStage
{
    Backlog,
    Plan,
    Build,
    Check,
    Integrate,
    Done
}

public enum IssueRuntimeStatus
{
    Active,
    Paused,
    Blocked,
    Interrupted,
    Closed,
    Completed
}

public enum MergeState
{
    Pending,
    Rebasing,
    Merging,
    Merged,
    BuildFailed,
    Conflict,
    Resolving,
    Blocked
}

[GenerateSerializer]
public class ApprovalState
{
    [Id(0)] public string Stage { get; set; } = null!;
    [Id(1)] public string Status { get; set; } = null!; // pending, awaiting, approved, rejected, error
    [Id(2)] public string? OutputJson { get; set; }
    [Id(3)] public string RequestedAt { get; set; } = null!;
    [Id(4)] public string? RespondedAt { get; set; }
}
