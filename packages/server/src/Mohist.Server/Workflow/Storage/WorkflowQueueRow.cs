namespace Mohist.Server.Workflow.Storage;

public class WorkflowQueueRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string State { get; set; } = WorkflowQueueStates.Queued;
    public string? RunnerId { get; set; }
    public string? WorkId { get; set; }
    public string? WorkType { get; set; }
    public string? Stage { get; set; }
    public string? LogicalId { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class WorkflowQueueStates
{
    public const string Queued = "queued";
    public const string Leased = "leased";
}
