namespace Mohist.Server.Infrastructure.Data.Workflow;

// Historical misnomer: `WorkflowRunProfileRow` (and the `WorkflowRunProfiles` DbSet /
// table `WorkflowRunProfiles`) actually stores Run-scoped Variables, never a Profile.
// The type was named before Run Variables were split out from WorkflowProfile and the
// class name now misleads readers. The class is preserved under a deliberate
// cost/benefit call rather than renamed via an EF Core migration (unfavorable
// cost/benefit for a cosmetic change). When the table is next restructured for a
// real reason, rename the row and DbSet in the same change.
public class WorkflowRunProfileRow
{
    public string WorkflowRunId { get; set; } = string.Empty;

    /// <summary>
    /// Explicit WorkflowRun VariableBundle JSON.
    /// </summary>
    public string Variables { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
