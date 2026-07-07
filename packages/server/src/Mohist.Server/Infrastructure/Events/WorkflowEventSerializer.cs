using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

internal static class WorkflowEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;

    public static string Type(WorkflowEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> for the workflow domain event.
    /// Legacy PascalCase class names are still emitted as the persisted
    /// <c>EventRow.Type</c> (via <see cref="Type"/>) for storage compatibility;
    /// the CloudEvents bus uses this mapping instead.
    /// </summary>
    public static string BusType(WorkflowEvent payload) => Unwrap(payload) switch
    {
        WorkflowRunStarted => EventCatalog.ReverseDns.WorkflowRunStarted,
        WorkflowRunResumed => EventCatalog.ReverseDns.WorkflowRunResumed,
        WorkflowRunPaused => EventCatalog.ReverseDns.WorkflowRunPaused,
        WorkflowRunStopped => EventCatalog.ReverseDns.WorkflowRunStopped,
        WorkflowRunCompleted => EventCatalog.ReverseDns.WorkflowRunCompleted,
        WorkflowRunFailed => EventCatalog.ReverseDns.WorkflowRunFailed,
        StageStarted => EventCatalog.ReverseDns.StageStarted,
        StageCompleted => EventCatalog.ReverseDns.StageCompleted,
        StageFailed => EventCatalog.ReverseDns.StageFailed,
        StageApprovalRequested => EventCatalog.ReverseDns.StageApprovalRequested,
        StageApprovalResolved => EventCatalog.ReverseDns.StageApprovalResolved,
        FeedbackRequested => EventCatalog.ReverseDns.FeedbackRequested,
        TaskStarted => EventCatalog.ReverseDns.TaskStarted,
        TaskCompleted => EventCatalog.ReverseDns.TaskCompleted,
        TaskFailed => EventCatalog.ReverseDns.TaskFailed,
        CheckPassed => EventCatalog.ReverseDns.CheckPassed,
        CheckFailed => EventCatalog.ReverseDns.CheckFailed,
        CheckPending => EventCatalog.ReverseDns.CheckPending,
        WorkflowArtifactRecorded => EventCatalog.ReverseDns.WorkflowArtifactRecorded,
        _ => throw new InvalidOperationException($"No CloudEvents type for {Unwrap(payload).GetType().Name}"),
    };

    /// <summary>
    /// Extract the workflow run id (source) and the issue number subject
    /// from a workflow run event source URI.
    /// </summary>
    public static (string WorkflowRunId, string? IssueNumber) ExtractContextFromSource(string source)
    {
        var prefix = "/mohist/workflow-runs/";
        if (!source.StartsWith(prefix, StringComparison.Ordinal))
            return (source, null);
        return (source[prefix.Length..], null);
    }

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
        nameof(FeedbackRequested) => data.Deserialize<FeedbackRequested>(JsonOptions)!,
        nameof(TaskStarted) => data.Deserialize<TaskStarted>(JsonOptions)!,
        nameof(TaskCompleted) => data.Deserialize<TaskCompleted>(JsonOptions)!,
        nameof(TaskFailed) => data.Deserialize<TaskFailed>(JsonOptions)!,
        nameof(CheckPassed) => data.Deserialize<CheckPassed>(JsonOptions)!,
        nameof(CheckFailed) => data.Deserialize<CheckFailed>(JsonOptions)!,
        nameof(CheckPending) => data.Deserialize<CheckPending>(JsonOptions)!,
        nameof(WorkflowArtifactRecorded) => data.Deserialize<WorkflowArtifactRecorded>(JsonOptions)!,
        _ => throw new InvalidOperationException($"Unknown workflow event '{type}'"),
    };

    public static object Unwrap(WorkflowEvent payload) => payload switch
    {
        WorkflowRunStarted x => (object)x,
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
        FeedbackRequested x => x,
        TaskStarted x => x,
        TaskCompleted x => x,
        TaskFailed x => x,
        CheckPassed x => x,
        CheckFailed x => x,
        CheckPending x => x,
        WorkflowArtifactRecorded x => x,
        null => throw new InvalidOperationException("Null workflow event"),
    };
}
