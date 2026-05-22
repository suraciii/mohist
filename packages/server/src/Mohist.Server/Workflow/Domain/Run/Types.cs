using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunStatus { Pending, Running, Paused, Passed, Failed, Cancelled }
public enum StageRunStatus { Pending, Running, AwaitingApproval, Passed, Failed }
public enum TaskRunStatus { Pending, Running, Completed, Failed }
public enum CheckRunStatus { Pending, Passed, Failed }
public enum FailureReason { TaskFailed, CheckUnrepaired, ApprovalRejected }

public sealed record FailureDetails(
    FailureReason Reason,
    string Stage,
    string? TaskId = null,
    string? CheckName = null,
    string? Message = null);

public sealed record ApprovalInput(JsonElement? Output = null);

public sealed record LoadedTaskInput(
    string Id,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public sealed record TaskResult(
    string Status,
    string? Reason = null);

public sealed record CheckResult(
    string Name,
    string Status,
    string? Message = null,
    JsonElement? Output = null);

public abstract record StageWork
{
    public sealed record StageInit(
        WorkflowTasksFromDefinition? TasksFrom) : StageWork;

    public sealed record Task(
        string Id,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : StageWork;

    public sealed record Check(
        string Name,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : StageWork;

    public sealed record AwaitApproval() : StageWork;
    public sealed record Complete() : StageWork;
    public sealed record Blocked(string Reason) : StageWork;
}

public abstract record WorkflowWork
{
    public sealed record StageInit(
        string Stage,
        WorkflowTasksFromDefinition? TasksFrom) : WorkflowWork;

    public sealed record Task(
        string Stage,
        string Id,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : WorkflowWork;

    public sealed record Check(
        string Stage,
        string Name,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : WorkflowWork;

    public sealed record AwaitApproval(string Stage) : WorkflowWork;
    public sealed record Complete(string Stage) : WorkflowWork;
    public sealed record Blocked(string Stage, string Reason) : WorkflowWork;
    public sealed record Failed(FailureDetails Reason) : WorkflowWork;
}

public sealed record TaskRunState(
    string Id,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    TaskRunStatus Status);

public sealed record CheckRunState(
    string Name,
    string Title,
    string? Uses,
    Dictionary<string, JsonElement?>? With,
    CheckRunStatus Status,
    string? Message,
    JsonElement? Output);

public sealed record ApprovalState(
    string Status,
    JsonElement? Output,
    string RequestedAt,
    string? RespondedAt);

public sealed record StageRunState(
    string Stage,
    StageRunStatus Status,
    int Order,
    List<TaskRunState> Tasks,
    List<CheckRunState> Checks,
    ApprovalState? Approval,
    FailureDetails? Failure);
