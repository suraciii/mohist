using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Domain.Run;

public enum FailureReason { TaskFailed, CheckUnrepaired, ApprovalRejected }

public sealed record FailureDetails(
    FailureReason Reason,
    string Stage,
    string? TaskId = null,
    string? CheckName = null,
    string? Message = null);

[GenerateSerializer]
public sealed record WorkLease(
    string WorkId,
    string WorkType,
    string Stage,
    string LogicalId,
    string? Title = null,
    string? RunnerId = null,
    WorkDispatch? Dispatch = null,
    DateTime? DispatchedAt = null);

public sealed record TaskResult(
    string Status,
    string? Reason = null);
