using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

internal static class WorkflowEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Type(WorkflowEvent payload) => Unwrap(payload).GetType().Name;

    public static JsonElement ToData(WorkflowEvent payload) =>
        JsonSerializer.SerializeToElement(Unwrap(payload), JsonOptions);

    public static WorkflowEvent FromData(string type, JsonElement data) => type switch
    {
        nameof(WorkflowRunStarted) => data.Deserialize<WorkflowRunStarted>(JsonOptions)!,
        nameof(WorkflowRunResumed) => data.Deserialize<WorkflowRunResumed>(JsonOptions)!,
        nameof(WorkflowRunPaused) => data.Deserialize<WorkflowRunPaused>(JsonOptions)!,
        nameof(WorkflowRunStopped) => data.Deserialize<WorkflowRunStopped>(JsonOptions)!,
        nameof(WorkflowRunCompleted) => data.Deserialize<WorkflowRunCompleted>(JsonOptions)!,
        nameof(WorkflowRunFailed) => data.Deserialize<WorkflowRunFailed>(JsonOptions)!,
        nameof(StageStarted) => data.Deserialize<StageStarted>(JsonOptions)!,
        nameof(StageCompleted) => data.Deserialize<StageCompleted>(JsonOptions)!,
        nameof(StageFailed) => data.Deserialize<StageFailed>(JsonOptions)!,
        nameof(StageApprovalRequested) => data.Deserialize<StageApprovalRequested>(JsonOptions)!,
        nameof(StageApprovalResolved) => data.Deserialize<StageApprovalResolved>(JsonOptions)!,
        nameof(TaskCompleted) => data.Deserialize<TaskCompleted>(JsonOptions)!,
        nameof(TaskFailed) => data.Deserialize<TaskFailed>(JsonOptions)!,
        nameof(CheckPassed) => data.Deserialize<CheckPassed>(JsonOptions)!,
        nameof(CheckFailed) => data.Deserialize<CheckFailed>(JsonOptions)!,
        nameof(CheckPending) => data.Deserialize<CheckPending>(JsonOptions)!,
        nameof(RepairScheduled) => data.Deserialize<RepairScheduled>(JsonOptions)!,
        _ => throw new InvalidOperationException($"Unknown workflow event '{type}'"),
    };

    public static object Unwrap(WorkflowEvent payload) => payload switch
    {
        WorkflowRunStarted x => x,
        WorkflowRunResumed x => x,
        WorkflowRunPaused x => x,
        WorkflowRunStopped x => x,
        WorkflowRunCompleted x => x,
        WorkflowRunFailed x => x,
        StageStarted x => x,
        StageCompleted x => x,
        StageFailed x => x,
        StageApprovalRequested x => x,
        StageApprovalResolved x => x,
        TaskCompleted x => x,
        TaskFailed x => x,
        CheckPassed x => x,
        CheckFailed x => x,
        CheckPending x => x,
        RepairScheduled x => x,
    };
}
