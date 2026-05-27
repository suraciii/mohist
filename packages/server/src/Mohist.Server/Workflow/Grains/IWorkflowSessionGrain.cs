namespace Mohist.Server.Workflow.Grains;

public interface IWorkflowSessionGrain : IGrainWithStringKey
{
    Task<WorkflowSessionSnapshot> EnsureAsync(EnsureWorkflowSessionCommand command);
    Task<WorkflowSessionSnapshot> AttachAcpSessionAsync(AttachAcpSessionCommand command);
    Task<IReadOnlyList<WorkflowSessionEventSnapshot>> AppendEventsAsync(AppendWorkflowSessionEventsCommand command);
    Task<WorkflowSessionSnapshot> MarkStatusAsync(WorkflowSessionStatusCommand command);
    Task<WorkflowSessionSnapshot> CompleteAsync(CompleteWorkflowSessionCommand command);
    Task<WorkflowSessionSnapshot?> GetAsync();
}

public static class WorkflowSessionGrainKeys
{
    public static string ForName(string workflowRunId, string sessionName) => $"{workflowRunId}:{sessionName}";
}

[GenerateSerializer]
public sealed record EnsureWorkflowSessionCommand(
    [property: Id(0)] string WorkflowRunId,
    [property: Id(1)] string SessionName,
    [property: Id(2)] string RunnerId,
    [property: Id(3)] string? ProjectId,
    [property: Id(4)] int? IssueNumber,
    [property: Id(5)] string WorkId,
    [property: Id(6)] string WorkType,
    [property: Id(7)] string? Stage,
    [property: Id(8)] string? Title);

[GenerateSerializer]
public sealed record AttachAcpSessionCommand(
    [property: Id(0)] string AcpSessionId,
    [property: Id(1)] string? WorkDir,
    [property: Id(2)] string? Model,
    [property: Id(3)] int? ProcessPid);

[GenerateSerializer]
public sealed record AppendWorkflowSessionEventsCommand(
    [property: Id(0)] string WorkId,
    [property: Id(1)] string WorkType,
    [property: Id(2)] string? Stage,
    [property: Id(3)] IReadOnlyList<WorkflowSessionEventInput> Events);

[GenerateSerializer]
public sealed record WorkflowSessionEventInput(
    [property: Id(0)] string Type,
    [property: Id(1)] string PayloadJson);

[GenerateSerializer]
public sealed record WorkflowSessionStatusCommand(
    [property: Id(0)] string Status,
    [property: Id(1)] DateTime? LastDataAt = null,
    [property: Id(2)] string? FailureReason = null);

[GenerateSerializer]
public sealed record CompleteWorkflowSessionCommand(
    [property: Id(0)] string Status,
    [property: Id(1)] string? FailureReason = null,
    [property: Id(2)] int? ExitCode = null);

[GenerateSerializer]
public sealed record WorkflowSessionSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string WorkflowRunId,
    [property: Id(2)] string SessionName,
    [property: Id(3)] string? AcpSessionId,
    [property: Id(4)] string? ProjectId,
    [property: Id(5)] int? IssueNumber,
    [property: Id(6)] string? RunnerId,
    [property: Id(7)] string Status,
    [property: Id(8)] string? Model,
    [property: Id(9)] string? WorkDir,
    [property: Id(10)] int? ProcessPid,
    [property: Id(11)] string CreatedAt,
    [property: Id(12)] string? StartedAt,
    [property: Id(13)] string? LastDataAt,
    [property: Id(14)] string? CompletedAt,
    [property: Id(15)] string? FailureReason,
    [property: Id(16)] int? ExitCode);

[GenerateSerializer]
public sealed record WorkflowSessionEventSnapshot(
    [property: Id(0)] string Id,
    [property: Id(1)] string WorkflowSessionId,
    [property: Id(2)] string WorkflowRunId,
    [property: Id(3)] string SessionName,
    [property: Id(4)] string? AcpSessionId,
    [property: Id(5)] string? ProjectId,
    [property: Id(6)] int? IssueNumber,
    [property: Id(7)] string? WorkId,
    [property: Id(8)] string? WorkType,
    [property: Id(9)] string? Stage,
    [property: Id(10)] long Sequence,
    [property: Id(11)] string Type,
    [property: Id(12)] string PayloadJson,
    [property: Id(13)] string CreatedAt);
