namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowBacklogGrain : IGrainWithStringKey
{
    Task RegisterAsync(string workflowId);
    Task<string?> ClaimAsync(string runnerId);
    Task ReleaseAsync(string workflowId);
    Task<IReadOnlyList<string>> ListWaitingAsync();
    Task<IReadOnlyList<(string WorkflowId, string RunnerId)>> ListRunningAsync();
    Task ClearAsync();
}

public static class WorkflowBacklogKeys
{
    public static string ForProject(string projectId) => projectId;
}

[GenerateSerializer]
public sealed record WorkflowBacklogState(
    [property: Id(0)] List<string> Waiting,
    [property: Id(1)] Dictionary<string, string> Running,
    [property: Id(2)] HashSet<string> All);
