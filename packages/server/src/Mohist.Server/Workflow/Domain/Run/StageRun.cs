namespace Mohist.Server.Workflow.Domain.Run;

public record StageRun(
    string StageId,
    int Order,
    int Attempt,
    bool RequiresApproval,
    StageRunPhase Phase,
    bool Initialized,
    IReadOnlyList<TaskRun> Tasks,
    IReadOnlyList<StageCheck> Checks,
    ApprovalState? Approval,
    FailureDetails? Failure);
