using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Runner.Grains;

public partial class RunnerGrain
{
    /// <summary>
    /// Resolves the issue reference carried on a workflow run's typed metadata.
    /// </summary>
    private static WorkIssueRef? IssueFromRun(WorkflowRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Metadata.ProjectId)
            || run.Metadata.IssueNumber is not > 0)
            return null;
        return new WorkIssueRef(run.Metadata.ProjectId, run.Metadata.IssueNumber.Value);
    }
}
