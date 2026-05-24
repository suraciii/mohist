using Mohist.Server.Events;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        app.MapGet("/api/issues/{number:int}/events", async (int number, string? projectId, int? limit, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListIssueEventsAsync(pid, number, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/issues/{number:int}/logs", async (int number, string? projectId, int? limit, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        app.MapGet("/api/workflows/{workflowRunId}/events", async (string workflowRunId, int? limit, IEventStore events) =>
        {
            var list = await events.ListWorkflowEventsAsync(workflowRunId, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/events/recent", async (string? projectId, int? limit, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListRecentAsync(pid, limit ?? 200);
            return ApiResults.Ok(list);
        });

        return app;
    }

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, IGrainFactory grains)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }
}
