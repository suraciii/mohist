namespace Mohist.Server.Workflow.Domain.Run;

public enum FailureReason { TaskFailed, CheckUnrepaired, ApprovalRejected }

public sealed record FailureDetails(
    FailureReason Reason,
    string Stage,
    string? TaskId = null,
    string? CheckName = null,
    string? Message = null);

public sealed record WorkLease(
    string WorkId,
    string WorkType,
    string Stage,
    string LogicalId,
    string? Title = null,
    string? RunnerId = null);
