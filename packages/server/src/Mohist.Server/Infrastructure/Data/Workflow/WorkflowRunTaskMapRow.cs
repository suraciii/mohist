namespace Mohist.Server.Infrastructure.Data.Workflow;

public sealed class WorkflowRunTaskMapRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string WorkId { get; set; } = string.Empty;
}
