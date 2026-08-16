namespace Mohist.Server.Workflow.Domain.Run;

public enum StageRunStatus { Pending, Running, AwaitingApproval, Completed, Failed }

public sealed record ApprovalStatus(
    string? Result,
    string RequestedAt,
    string? RespondedAt,
    string? DecidedBy = null,
    string? DisplayName = null);

public sealed class StageRun
{
    public required string Id { get; init; }
    public required int Attempt { get; init; }
    public required bool RequiresApproval { get; init; }
    public StageRunStatus Status { get; set; }
    public bool Initialized { get; set; }
    public List<TaskRun> Tasks { get; set; } = new();
    public List<StageCheck> Checks { get; set; } = new();
    public string? ChecksWorkId { get; set; }
    public WorkInterruption? Interruption { get; set; }
    public ApprovalStatus? ApprovalStatus { get; set; }
    public FailureDetails? Failure { get; set; }
}
