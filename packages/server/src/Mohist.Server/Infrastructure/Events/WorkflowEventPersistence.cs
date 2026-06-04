using Microsoft.EntityFrameworkCore;
using Mohist.Server.Infrastructure.Persistence.Db;
using Mohist.Server.Infrastructure.Persistence.Events;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.Infrastructure.Events;

internal static class WorkflowEventPersistence
{
    public const string SpecVersion = "1.0";

    public static async Task<IReadOnlyList<StagedWorkflowEvent>> StageAsync(
        MohistDbContext db,
        string workflowRunId,
        IReadOnlyList<WorkflowEvent> events,
        CancellationToken ct = default)
    {
        if (events.Count == 0) return [];

        var source = WorkflowRunSource(workflowRunId);
        var nextId = (await db.Events
            .Where(e => e.Source == source)
            .Select(e => (long?)e.Id)
            .MaxAsync(ct) ?? 0) + 1;
        var staged = new List<StagedWorkflowEvent>(events.Count);

        foreach (var payload in events)
        {
            var type = WorkflowEventSerializer.Type(payload);
            var row = new EventRow
            {
                Source = source,
                Id = nextId++,
                Data = WorkflowEventSerializer.ToData(payload),
                WorkflowEvent = payload,
            };

            db.Events.Add(row);
            staged.Add(new StagedWorkflowEvent(row, type, payload));
        }

        return staged;
    }

    public static WorkflowDomainEventDto ToDto(StagedWorkflowEvent staged) => new(
        staged.Row.Id,
        staged.Row.Source,
        staged.Type,
        staged.Payload,
        staged.Row.Time,
        SpecVersion);

    public static WorkflowDomainEventDto ToDto(EventRow row, string type, string specVersion) => new(
        row.Id,
        row.Source,
        type,
        row.WorkflowEvent ?? WorkflowEventSerializer.FromData(type, row.Data),
        row.Time,
        specVersion);

    public static string WorkflowRunSource(string workflowRunId) => $"/workflow-runs/{workflowRunId}";
}

internal sealed record StagedWorkflowEvent(EventRow Row, string Type, WorkflowEvent Payload);
