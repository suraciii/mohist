namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowStageLockGrain : IGrainWithStringKey
{
    Task<StageLockAcquireResult> AcquireSequentialAsync(StageLockRequest request);
    Task<StageLockReleaseResult> ReleaseAsync(StageLockOwner owner);
    Task<WorkflowStageLockState?> GetStateAsync();
}

[GenerateSerializer]
public sealed record StageLockRequest(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Stage,
    [property: Id(2)] string Resource,
    [property: Id(3)] string ProjectId);

[GenerateSerializer]
public sealed record StageLockOwner(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string Stage);

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

[GenerateSerializer]
public sealed class WorkflowStageLockState
{
    [Id(0)] public StageLockOwner? Owner { get; set; }
    [Id(1)] public List<StageLockRequest> Waiting { get; set; } = [];
}

public static class WorkflowStageLockKeys
{
    public static string ForProjectResource(string projectId, string resource) =>
        $"project:{projectId}:{resource}";
}
