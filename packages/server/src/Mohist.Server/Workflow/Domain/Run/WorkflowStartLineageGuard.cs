using Orleans;

namespace Mohist.Server.Workflow.Domain.Run;

[GenerateSerializer]
public sealed record WorkflowStartLineageGuard(
    [property: Id(0)] string IssueId,
    [property: Id(1)] long IssueLineageVersion);

public sealed class WorkflowStartLineageChangedException : Exception
{
    public WorkflowStartLineageChangedException(string issueId)
        : base($"Issue '{issueId}' lineage changed while creating its workflow run.")
    {
    }
}
