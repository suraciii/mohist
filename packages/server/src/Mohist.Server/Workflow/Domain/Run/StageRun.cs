namespace Mohist.Server.Workflow.Domain.Run;

public enum StageRunStatus { Pending, Running, AwaitingApproval, Completed, Failed }

public sealed record ApprovalStatus(
    string Status,
    string RequestedAt,
    string? RespondedAt);

public sealed class StageRun
{
    public required string StageId { get; init; }
    public required int Attempt { get; init; }
    public required bool RequiresApproval { get; init; }
    public StageRunStatus Status { get; set; }
    public bool Initialized { get; set; }
    public List<TaskRun> Tasks { get; set; } = new();
    public List<StageCheck> Checks { get; set; } = new();
    public ApprovalStatus? Approval { get; set; }
    public FailureDetails? Failure { get; set; }
}
