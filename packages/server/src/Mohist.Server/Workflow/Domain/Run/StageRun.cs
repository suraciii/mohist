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
    /// Remaining automatic onFailure recovery credits per task definition.
    /// Created lazily when a task with onFailure fails; reset when the user
    /// manually retries the task. Each successful recovery injection consumes
    /// one credit.
    /// </summary>
    public Dictionary<string, int> RecoveryBudget { get; set; } = new();
}
