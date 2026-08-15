using System.Text.Json;
using Mohist.Server.Infrastructure.Data.Events;
using Mohist.Server.Workflow.Domain.Artifacts;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

internal static class WorkflowEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = JSON.Options;
    private static readonly IReadOnlyDictionary<Type, string> BusTypes = new Dictionary<Type, string>
    {
        [typeof(WorkflowRunStarted)] = EventCatalog.ReverseDns.WorkflowRunStarted,
        [typeof(WorkflowRunResumed)] = EventCatalog.ReverseDns.WorkflowRunResumed,
        [typeof(WorkflowRunPaused)] = EventCatalog.ReverseDns.WorkflowRunPaused,
        [typeof(WorkflowRunStopped)] = EventCatalog.ReverseDns.WorkflowRunStopped,
        [typeof(WorkflowRunCompleted)] = EventCatalog.ReverseDns.WorkflowRunCompleted,
        [typeof(WorkflowRunFailed)] = EventCatalog.ReverseDns.WorkflowRunFailed,
        [typeof(StageStarted)] = EventCatalog.ReverseDns.StageStarted,
        [typeof(StageCompleted)] = EventCatalog.ReverseDns.StageCompleted,
        [typeof(StageFailed)] = EventCatalog.ReverseDns.StageFailed,
        [typeof(StageApprovalRequested)] = EventCatalog.ReverseDns.StageApprovalRequested,
        [typeof(StageApprovalResolved)] = EventCatalog.ReverseDns.StageApprovalResolved,
        [typeof(FeedbackRequested)] = EventCatalog.ReverseDns.FeedbackRequested,
        [typeof(TaskStarted)] = EventCatalog.ReverseDns.TaskStarted,
        [typeof(TaskCompleted)] = EventCatalog.ReverseDns.TaskCompleted,
        [typeof(TaskFailed)] = EventCatalog.ReverseDns.TaskFailed,
        [typeof(TaskInterrupted)] = EventCatalog.ReverseDns.TaskInterrupted,
        [typeof(TaskCancelled)] = EventCatalog.ReverseDns.TaskCancelled,
        [typeof(AgentTaskUpdateInterrupted)] = EventCatalog.ReverseDns.AgentTaskUpdateInterrupted,
        [typeof(AgentTaskResultUnconfirmed)] = EventCatalog.ReverseDns.AgentTaskResultUnconfirmed,
        [typeof(TaskBlocked)] = EventCatalog.ReverseDns.TaskBlocked,
        [typeof(StageBlocked)] = EventCatalog.ReverseDns.StageBlocked,
        [typeof(WorkflowRunBlocked)] = EventCatalog.ReverseDns.WorkflowRunBlocked,
        [typeof(CheckPassed)] = EventCatalog.ReverseDns.CheckPassed,
        [typeof(CheckFailed)] = EventCatalog.ReverseDns.CheckFailed,
        [typeof(CheckPending)] = EventCatalog.ReverseDns.CheckPending,
        [typeof(ChecksInterrupted)] = EventCatalog.ReverseDns.ChecksInterrupted,
        [typeof(WorkflowArtifactRecorded)] = EventCatalog.ReverseDns.WorkflowArtifactRecorded,
    };

    internal static IReadOnlyCollection<string> ProducedTypes => BusTypes.Values.ToArray();

    public static string Type(WorkflowEvent payload) => Unwrap(payload).GetType().Name;

    /// <summary>
    /// CloudEvents 1.0.2 reverse-DNS <c>type</c> for the workflow domain event.
    /// Legacy PascalCase class names are still emitted as the persisted
    /// <c>EventRow.Type</c> (via <see cref="Type"/>) for storage compatibility;
    /// the CloudEvents bus uses this mapping instead.
    /// </summary>
    public static string BusType(WorkflowEvent payload)
    {
        var variant = Unwrap(payload);
        return BusTypes.TryGetValue(variant.GetType(), out var type)
            ? type
            : throw new InvalidOperationException($"No CloudEvents type for {variant.GetType().Name}");
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
        nameof(TaskInterrupted) => data.Deserialize<TaskInterrupted>(JsonOptions)!,
        nameof(TaskCancelled) => data.Deserialize<TaskCancelled>(JsonOptions)!,
        nameof(AgentTaskUpdateInterrupted) => data.Deserialize<AgentTaskUpdateInterrupted>(JsonOptions)!,
        nameof(AgentTaskResultUnconfirmed) => data.Deserialize<AgentTaskResultUnconfirmed>(JsonOptions)!,
        nameof(TaskBlocked) => data.Deserialize<TaskBlocked>(JsonOptions)!,
        nameof(StageBlocked) => data.Deserialize<StageBlocked>(JsonOptions)!,
        nameof(WorkflowRunBlocked) => data.Deserialize<WorkflowRunBlocked>(JsonOptions)!,
        nameof(CheckPassed) => data.Deserialize<CheckPassed>(JsonOptions)!,
        nameof(CheckFailed) => data.Deserialize<CheckFailed>(JsonOptions)!,
        nameof(CheckPending) => data.Deserialize<CheckPending>(JsonOptions)!,
        nameof(ChecksInterrupted) => data.Deserialize<ChecksInterrupted>(JsonOptions)!,
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
        TaskInterrupted x => x,
        TaskCancelled x => x,
        AgentTaskUpdateInterrupted x => x,
        AgentTaskResultUnconfirmed x => x,
        TaskBlocked x => x,
        StageBlocked x => x,
        WorkflowRunBlocked x => x,
        CheckPassed x => x,
        CheckFailed x => x,
        CheckPending x => x,
        ChecksInterrupted x => x,
        WorkflowArtifactRecorded x => x,
        null => throw new InvalidOperationException("Null workflow event"),
    };
}
