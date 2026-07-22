namespace Mohist.Server.Infrastructure.Data.Workflow;

public class WorkflowRunProfileRow
{
    public string WorkflowRunId { get; set; } = string.Empty;

    /// <summary>
    /// Explicit WorkflowRun VariableBundle JSON.
    /// </summary>
    public string Variables { get; set; } = "{}";

    /// <summary>
    /// Initialization-default VariableBundle JSON. Entries here resolve
    /// below Project, Issue, explicit Run, and selected-stage overlays so
    /// any explicit write wins. Issue-474 T-002 seeds
    /// <c>vars.archive = ""</c> here on WorkflowRun creation; the marker is
    /// removed once the key is set by an explicit Run write (setVars, PUT,
    /// or PATCH).
    /// </summary>
    public string DefaultVariables { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
