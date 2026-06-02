using Mohist.Server.Runner.Grains;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowBacklogGrain : IGrainWithStringKey
{
    Task EnqueueAsync(string workflowRunId);
    Task<string?> ClaimAsync(string runnerId);
}

public static class WorkflowBacklogKeys
{
    public static string ForProject(string projectId) => projectId;
}

[GenerateSerializer]
public sealed record WorkflowBacklogState(
    [property: Id(0)] List<string> Waiting,
    [property: Id(1)] HashSet<string> All);
