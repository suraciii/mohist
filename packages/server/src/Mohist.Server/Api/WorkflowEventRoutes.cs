using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Project.Querying;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    

    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        app.MapGet("/api/issues/{number:int}/events", async (int number, string projectId, int? limit, IGrainFactory grains, IEventStore events, ProjectQuerier projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListIssueEventsAsync(pid, number, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/issues/{number:int}/workflow-log", async (int number, string projectId, IEventStore events) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListIssueWorkflowLogAsync(pid, number);
            return ApiResults.Ok(new WorkflowLogResponse(list));
        });

        app.MapGet("/api/issues/{number:int}/logs", async (int number, string projectId, int? limit, IGrainFactory grains, IEventStore events, ProjectQuerier projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListIssueEventsAsync(pid, number, limit ?? 200);
            return ApiResults.Ok(list.Select(e => new
            {
                id = e.Id,
                eventType = e.Type,
                data = e,
                createdAt = e.CreatedAt,
            }));
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/events", async (string workflowRunId, int? limit, IEventStore events) =>
        {
            var list = await events.ListWorkflowEventsAsync(workflowRunId, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/events/recent", async (string projectId, int? limit, IGrainFactory grains, IEventStore events, ProjectQuerier projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListRecentAsync(pid, limit ?? 200);
            return ApiResults.Ok(list);
        });

        return app;
    }
}
