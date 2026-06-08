namespace Mohist.Server.Workflow.Domain.Run;

public enum StageRunStatus { Pending, Running, AwaitingApproval, Completed, Failed }

public sealed record ApprovalStatus(
    string? Result,
    string RequestedAt,
    string? RespondedAt);

public sealed class StageRun
{
    public required string Id { get; init; }
    public required int Attempt { get; init; }
    public required bool RequiresApproval { get; init; }
    public StageRunStatus Status { get; set; }
    public bool Initialized { get; set; }
    public List<TaskRun> Tasks { get; set; } = new();
    public List<StageCheck> Checks { get; set; } = new();
    public ApprovalStatus? ApprovalStatus { get; set; }
    public FailureDetails? Failure { get; set; }
    /// <summary>
    /// Last rejection reason from an AwaitingApproval stage, preserved
    /// across the Rerun cycle so the operator can see why the previous
    /// attempt was rejected. Cleared on the next successful Approve or
    /// when the stage transitions out of AwaitingApproval.
    /// </summary>
    public string? LastRejectionReason { get; set; }
}
