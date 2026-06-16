namespace Mohist.Server.Workflow.Grains;

/// <summary>
/// Thrown when a workflow retry or task dispatch is rejected because the
/// associated agent session's context window usage is at or above the
/// block threshold. The HTTP layer translates this exception into a 409
/// response that lists the recommended recovery actions
/// (<c>compact</c>, <c>reset</c>).
/// </summary>
public sealed class WorkflowSessionContextExhaustedException : Exception
{
    public double? ContextUsagePercent { get; }
    public string? Stage { get; }
    public string? TaskId { get; }

    public WorkflowSessionContextExhaustedException(string message, double? contextUsagePercent, string? stage, string? taskId)
        : base(message)
    {
        ContextUsagePercent = contextUsagePercent;
        Stage = stage;
        TaskId = taskId;
    }
}
