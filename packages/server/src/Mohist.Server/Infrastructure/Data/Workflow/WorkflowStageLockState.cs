namespace Mohist.Server.Infrastructure.Data.Workflow;

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
public sealed class WorkflowStageLockState
{
    [Id(0)] public StageLockOwner? Owner { get; set; }
    [Id(1)] public List<StageLockRequest> Waiting { get; set; } = [];
}
