using Mohist.Server.Events;
using Mohist.Server.Project.Grains;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        app.MapGet("/api/issues/{number:int}/events", async (int number, int? limit, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListIssueEventsAsync(pid, number, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/issues/{number:int}/logs", async (int number, int? limit, IGrainFactory grains, IEventStore events) =>
        {
            var pid = await ResolveProjectIdAsync(grains);
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
            var pid = !string.IsNullOrWhiteSpace(projectId) ? projectId : await ResolveProjectIdAsync(grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var list = await events.ListRecentAsync(pid, limit ?? 200);
            return ApiResults.Ok(list);
        });

        return app;
    }

    private static async Task<string?> ResolveProjectIdAsync(IGrainFactory grains)
    {
        var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
        var current = await registry.GetCurrentAsync();
        return current?.Id;
    }
}
