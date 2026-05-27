using Mohist.Server.Workflow.Sessions.Queries;

namespace Mohist.Server.Api;

public static class WorkflowSessionRoutes
{
    public static WebApplication MapWorkflowSessionRoutes(this WebApplication app)
    {
        app.MapGet("/api/workflows/{workflowRunId}/sessions", async (string workflowRunId, WorkflowSessionQueryService sessions) =>
            ApiResults.Ok(await sessions.ListByWorkflowAsync(workflowRunId)));

        app.MapGet("/api/workflows/{workflowRunId}/sessions/{sessionName}", async (string workflowRunId, string sessionName, WorkflowSessionQueryService sessions) =>
        {
            var detail = await sessions.GetByWorkflowAsync(workflowRunId, sessionName);
            return detail is null ? ApiResults.NotFound($"Session {sessionName} not found") : ApiResults.Ok(detail);
        });

        app.MapGet("/api/issues/{number:int}/workflow-sessions", async (int number, string projectId, WorkflowSessionQueryService sessions) =>
            ApiResults.Ok(await sessions.ListByIssueAsync(projectId, number)));

        return app;
    }
}
