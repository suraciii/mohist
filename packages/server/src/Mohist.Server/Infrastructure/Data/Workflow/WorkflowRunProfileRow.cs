namespace Mohist.Server.Infrastructure.Data.Workflow;

public class WorkflowRunProfileRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string Variables { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
