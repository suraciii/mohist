namespace Mohist.Server.Workflow.Storage;

public class WorkflowStageLockRow
{
    public string Key { get; set; } = string.Empty;
    public string State { get; set; } = "{}";
}
