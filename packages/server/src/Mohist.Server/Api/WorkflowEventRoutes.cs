using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Events;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static class WorkflowEventRoutes
{
    public static WebApplication MapWorkflowEventRoutes(this WebApplication app)
    {
        var byProject = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byProject.MapGet("/events", async (HttpContext context, int number, int? limit, IssueQuerier issues, IEventStore events) =>
        {
            var project = context.GetResolvedProject();

            var issue = await issues.GetInfoAsync(project.Id, number);
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
