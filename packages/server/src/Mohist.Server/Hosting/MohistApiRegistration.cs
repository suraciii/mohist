using Mohist.Server.Api;

namespace Mohist.Server.Hosting;

public static class MohistApiRegistration
{
    public static WebApplication MapMohistApi(this WebApplication app)
    {
        app.UseApiExceptionHandler();
        app.MapHealthRoutes();
        app.MapStatusRoutes();
        app.MapProjectRoutes();
        app.MapIssueRoutes();
        app.MapWorkflowProfileRoutes();
        app.MapWorkflowEventRoutes();
        app.MapWorkflowSessionRoutes();
        app.MapWorkflowTaskRoutes();
        app.MapEventRoutes();
        app.MapConfigRoutes();
        app.MapOpencodeRoutes();
        app.MapLabelsRoutes();
        app.MapLogsRoutes();
        app.MapFsRoutes();
        app.MapWorkspaceRoutes();
        app.MapEpicRoutes();
        app.MapAgentRoutes();
        app.MapRunnerRoutes();
        return app;
    }
}
