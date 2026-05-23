using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunStatus { Pending, Running, AwaitingApproval, Paused, Passed, Failed }

public sealed record WorkLease(
    string WorkId,
    string WorkType,
    string Stage,
    string LogicalId);
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

public sealed record CheckItem(
    string Name,
    string Title,
    string? Uses = null,
    Dictionary<string, JsonElement?>? With = null);

public abstract record StageWork
{
    public sealed record StageInit() : StageWork;

    public sealed record Task(
        string Id,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : StageWork;

    public sealed record Checks(List<CheckItem> Items) : StageWork;
}

public abstract record WorkflowWork
{
    public sealed record StageInit(string Stage) : WorkflowWork;

    public sealed record Task(
        string Stage,
        string Id,
        string Title,
        string? Uses = null,
        Dictionary<string, JsonElement?>? With = null) : WorkflowWork;

    public sealed record Checks(
        string Stage,
        List<CheckItem> Items) : WorkflowWork;
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
