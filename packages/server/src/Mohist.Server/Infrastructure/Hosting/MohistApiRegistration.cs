using Mohist.Server.Api;
using Mohist.Server.Events.Hub;
using Mohist.Server.Runner.Services.SignalR;

namespace Mohist.Server.Infrastructure.Hosting;

public static class MohistApiRegistration
{
    public static WebApplication MapMohistApi(this WebApplication app)
    {
        app.UseApiExceptionHandler();
        app.MapHealthRoutes();
        app.MapStatusRoutes();
        app.MapProjectRoutes();
        app.MapAttachmentRoutes();
        app.MapIssueRoutes();
        app.MapWorkflowEventRoutes();
        app.MapWorkflowSessionRoutes();
        app.MapWorkflowTaskRoutes();
        app.MapConfigRoutes();
        app.MapSystemRoutes();
        app.MapTemplateRoutes();
        app.MapIssueTemplateRoutes();
        app.MapOpencodeRoutes();
        app.MapLabelsRoutes();
        app.MapLogsRoutes();
        app.MapFsRoutes();
        app.MapWorkspaceRoutes();
        app.MapEpicRoutes();
        app.MapAgentDefinitionRoutes();
        app.MapAgentRoutes();
        app.MapAgentJobRoutes();
        app.MapAgentSessionLaunchRoutes();
        app.MapAgentSessionFollowupRoutes();
        app.MapAgentSessionCancelRoutes();
        app.MapRunnerRoutes();
        app.MapRunnerStatusRoutes();
        app.MapRunnerIdentityRoutes();
        app.MapWorkflowArtifactUploadRoutes();
        app.MapHub<RunnerHub>("/hubs/runner");
        app.MapHub<MohistHub>("/hubs/events");
        app.MapOtlpRoutes();
        app.MapOtelQueryRoutes();
        return app;
    }
}
