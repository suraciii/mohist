using Mohist.Server.Sessions.Services;
using Mohist.Server.Project.Services;

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

        app.MapGet("/api/projects/{projectRef}/issues/{number:int}/workflow-sessions", async (string projectRef, int number, AgentSessionQuerier sessions, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            return project is null
                ? ApiResults.NotFound("Project not found")
                : ApiResults.Ok(await sessions.ListByIssueAsync(project.Id, number));
        });

        return app;
    }
}
