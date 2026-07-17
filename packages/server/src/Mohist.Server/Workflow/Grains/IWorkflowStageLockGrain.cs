using Mohist.Server.Infrastructure.Data.Workflow;

namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowStageLockGrain : IGrainWithStringKey
{
    Task<StageLockAcquireResult> AcquireSequentialAsync(StageLockRequest request);
    Task<StageLockReleaseResult> ReleaseAsync(StageLockOwner owner);
    Task<WorkflowStageLockState?> GetStateAsync();
}

[GenerateSerializer]
public sealed record StageLockAcquireResult(
    [property: Id(0)] bool Acquired,
    [property: Id(1)] string Resource,
    [property: Id(2)] string? OwnerWorkflowRunId,
    [property: Id(3)] int WaitingCount);

[GenerateSerializer]
public sealed record StageLockReleaseResult(
    [property: Id(0)] bool Released,
    [property: Id(1)] string Resource,
    [property: Id(2)] string? NextWorkflowRunId,
    [property: Id(3)] int WaitingCount);

public static class WorkflowStageLockKeys
{
    public static string ForProjectResource(string projectId, string resource) =>
        $"project:{projectId}:{resource}";

    public static string ForProjectRepositoryResource(string projectId, string repositoryName, string resource) =>
        $"project-repository:v1:{Encode(projectId)}{Encode(repositoryName)}{Encode(resource)}";

    private static string Encode(string value) => $"{value.Length}:{value}";
}
