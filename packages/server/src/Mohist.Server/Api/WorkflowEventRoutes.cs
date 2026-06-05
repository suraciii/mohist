using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        app.MapGet("/api/issues/{number:int}/events", async (int number, string projectId, int? limit, IssueQuerier issues, IEventStore events) =>
        {
            if (string.IsNullOrWhiteSpace(projectId)) return ApiResults.BadRequest("No active project");

            var issue = await issues.GetInfoAsync(projectId, number);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId)) return ApiResults.Ok(Array.Empty<WorkflowDomainEventDto>());

            var list = await events.ListWorkflowEventsAsync(issue.WorkflowRunId, limit ?? 200);
            return ApiResults.Ok(list);
        });

        app.MapGet("/api/workflow-runs/{workflowRunId}/events", async (string workflowRunId, int? limit, IEventStore events) =>
        {
            var list = await events.ListWorkflowEventsAsync(workflowRunId, limit ?? 200);
            return ApiResults.Ok(list);
        });

        return app;
    }
}
