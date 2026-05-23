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
    public const string Key = "default";
}
