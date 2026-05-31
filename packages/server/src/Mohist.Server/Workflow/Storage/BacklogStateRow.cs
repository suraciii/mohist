namespace Mohist.Server.Workflow.Storage;

public class BacklogStateRow
{
    public string ProjectId { get; set; } = string.Empty;
    public string StateJson { get; set; } = "{}";
}
