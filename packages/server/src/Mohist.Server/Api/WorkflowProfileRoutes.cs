using Mohist.Server.Issue.WorkflowProfiles;

namespace Mohist.Server.Api;

public static class WorkflowProfileRoutes
{
    public static WebApplication MapWorkflowProfileRoutes(this WebApplication app)
    {
        app.MapGet("/api/workflow-profiles", (IssueWorkflowProfileRegistry profiles) =>
            ApiResults.Ok(profiles.List()));

        return app;
    }
}
