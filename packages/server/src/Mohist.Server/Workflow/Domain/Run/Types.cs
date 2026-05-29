using System.Text.Json;
using Mohist.Server.Workflow.Domain.Definition;

namespace Mohist.Server.Workflow.Domain.Run;

public enum WorkflowRunPhase { Pending, Running, AwaitingApproval, Paused, Completed, Failed }
public enum StageRunPhase { Pending, Running, AwaitingApproval, Completed, Failed }
public enum TaskRunPhase { Pending, Running, Completed, Failed }
public enum CheckRunPhase { Pending, Passed, Failed }
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

public sealed record ApprovalState(
    string Status,
    JsonElement? Output,
    string RequestedAt,
    string? RespondedAt);

public sealed record WorkflowRunMetadata(
    string? Name,
    DateTimeOffset CreatedAt,
    Dictionary<string, string>? Labels = null,
    Dictionary<string, string>? Annotations = null);

public sealed record WorkLease(
    string WorkId,
    string WorkType,
    string Stage,
    string LogicalId,
    string? RunnerId = null);
