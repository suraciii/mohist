namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowWorkDeliveryStatus { Pending, Started, Completed, Failed }

public sealed record WorkflowWorkDelivery(
    string WorkId,
    string WorkType,
    string Stage,
    WorkflowWorkDeliveryStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null);
