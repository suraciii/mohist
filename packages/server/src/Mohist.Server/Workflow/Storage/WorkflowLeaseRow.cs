namespace Mohist.Server.Workflow.Storage;

public class WorkflowLeaseRow
{
    public string WorkflowRunId { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
}
