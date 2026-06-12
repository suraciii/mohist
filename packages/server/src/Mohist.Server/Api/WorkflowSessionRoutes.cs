using Microsoft.AspNetCore.Http;
using Mohist.Server.Workflow.Services.Sessions;

namespace Mohist.Server.Api;

public static class WorkflowSessionRoutes
{
    public static WebApplication MapWorkflowSessionRoutes(this WebApplication app)
    {
        app.MapGet("/api/workflow-runs/{workflowRunId}/sessions", async (string workflowRunId, AgentSessionQuerier sessions) =>
            ApiResults.Ok(await sessions.ListByWorkflowAsync(workflowRunId)));

        app.MapGet("/api/workflow-runs/{workflowRunId}/sessions/{sessionName}", async (string workflowRunId, string sessionName, AgentSessionQuerier sessions) =>
        {
            var detail = await sessions.GetByWorkflowAsync(workflowRunId, sessionName);
            return detail is null ? ApiResults.NotFound($"Session {sessionName} not found") : ApiResults.Ok(detail);
        });

        var byProject = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        byProject.MapGet("/workflow-sessions", async (HttpContext context, int number, AgentSessionQuerier sessions) =>
        {
            var project = context.GetResolvedProject();
            return ApiResults.Ok(await sessions.ListByIssueAsync(project.Id, number));
        });

        return app;
    }
}
