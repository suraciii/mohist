using System.Text.Json;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Infrastructure.Hosting;
using Mohist.Server.Infrastructure.Data.Workflow;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Workflow.Services;

public sealed class WorkflowEventQuerier : IScopedService
{
    private readonly IEventStore _events;
    private readonly IWorkflowRunStore _runs;

    public WorkflowEventQuerier(IEventStore events, IWorkflowRunStore runs)
    {
        _events = events;
        _runs = runs;
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(
        string projectId,
        int issueNumber,
        string? workflowRunId,
        int limit,
        CancellationToken ct = default)
    {
        var issueEvents = await _events.ListIssueEventsAsync(projectId, issueNumber, limit, ct);
        if (workflowRunId is null)
            return issueEvents;

        var workflowEvents = await ListWorkflowEventsAsync(workflowRunId, limit, ct);
        return issueEvents
            .Concat(workflowEvents)
            .OrderBy(e => e.Envelope.Time)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListWorkflowEventsAsync(
        string workflowRunId,
        int limit,
        CancellationToken ct = default)
    {
        var filtered = await ListValidWorkflowEventsAsync(workflowRunId, ct);
        if (filtered.Count <= limit)
            return filtered;

        return filtered
            .Skip(filtered.Count - limit)
            .ToList();
    }

    public async Task<IReadOnlyList<StoredCloudEvent>> ListValidWorkflowEventsAsync(
        string workflowRunId,
        CancellationToken ct = default)
    {
        var run = await _runs.LoadAsync(workflowRunId, ct);
        var events = await _events.ListAsync(workflowRunId, int.MaxValue, ct);
        return FilterInvalidatedControlEvents(events, run);
    }

    private static IReadOnlyList<StoredCloudEvent> FilterInvalidatedControlEvents(
        IReadOnlyList<StoredCloudEvent> events,
        WorkflowRun? run)
    {
        if (run is null || run.Stages.Count == 0 || events.Count == 0)
            return events;

        var stageIndexes = run.Stages
            .Select((stage, index) => (stage.Id, index))
            .ToDictionary(x => x.Id, x => x.index, StringComparer.Ordinal);
        var stageIds = run.Stages.Select(s => s.Id).ToList();
        var seenStartedStages = new HashSet<string>(StringComparer.Ordinal);
        var validFromEventId = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var stored in events.OrderBy(e => e.Id))
        {
            if (!IsEventType(stored, EventCatalog.ReverseDns.StageStarted)
                || !TryGetEventStage(stored, out var stage)
                || !stageIndexes.TryGetValue(stage, out var targetIndex))
                continue;

            if (seenStartedStages.Add(stage))
                continue;

            for (var i = targetIndex; i < stageIds.Count; i++)
                validFromEventId[stageIds[i]] = stored.Id;
        }

        if (validFromEventId.Count == 0)
            return events;

        return events
            .Where(e => !ShouldHideInvalidatedControlEvent(e, validFromEventId))
            .ToList();
    }

    private static bool ShouldHideInvalidatedControlEvent(
        StoredCloudEvent stored,
        IReadOnlyDictionary<string, long> validFromEventId)
    {
        if (!IsStageTaskOrCheckControlEvent(stored) || !TryGetEventStage(stored, out var stage))
            return false;

        return validFromEventId.TryGetValue(stage, out var validFrom)
            && stored.Id < validFrom;
    }

    private static bool IsStageTaskOrCheckControlEvent(StoredCloudEvent stored)
    {
        var type = stored.Envelope.Type;
        return type is EventCatalog.ReverseDns.StageStarted
            or EventCatalog.ReverseDns.StageCompleted
            or EventCatalog.ReverseDns.StageFailed
            or EventCatalog.ReverseDns.StageApprovalRequested
            or EventCatalog.ReverseDns.StageApprovalResolved
            or EventCatalog.ReverseDns.FeedbackRequested
            or EventCatalog.ReverseDns.TaskStarted
            or EventCatalog.ReverseDns.TaskCompleted
            or EventCatalog.ReverseDns.TaskFailed
            or EventCatalog.ReverseDns.TaskCancelled
            or EventCatalog.ReverseDns.AgentTaskResultUnconfirmed
            or EventCatalog.ReverseDns.TaskBlocked
            or EventCatalog.ReverseDns.StageBlocked
            or EventCatalog.ReverseDns.WorkflowRunBlocked
            or EventCatalog.ReverseDns.CheckPassed
            or EventCatalog.ReverseDns.CheckFailed
            or EventCatalog.ReverseDns.CheckPending
            or EventCatalog.ReverseDns.RepairScheduled;
    }

    private static bool IsEventType(StoredCloudEvent stored, string type) =>
        string.Equals(stored.Envelope.Type, type, StringComparison.Ordinal);

    private static bool TryGetEventStage(StoredCloudEvent stored, out string stage)
    {
        stage = "";
        var data = stored.Envelope.Data;
        if (data is not JsonElement element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("stage", out var stageProperty)
            || stageProperty.ValueKind != JsonValueKind.String)
            return false;

        stage = stageProperty.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(stage);
    }
}
