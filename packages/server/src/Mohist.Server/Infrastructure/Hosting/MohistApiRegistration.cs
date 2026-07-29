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
        app.MapDeadLetterRoutes();
        app.MapProjectRoutes();
        app.MapProjectEventsRoutes();
        app.MapActivityRoutes();
        app.MapProjectEventTailRoutes();
        app.MapAttachmentRoutes();
        app.MapIssueRoutes();
        app.MapWorkflowEventRoutes();
        app.MapWorkflowSessionRoutes();
        app.MapUnifiedSessionRoutes();
        app.MapWorkflowTaskRoutes();
        app.MapWorkflowRunControlRoutes();
        app.MapWorkflowRunDetailRoute();
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
        app.MapInboxRoutes();
        app.MapAgentDefinitionRoutes();
        app.MapAgentRoutes();
        app.MapAgentJobRoutes();
        app.MapAgentJobReadRoutes();
        app.MapAgentSessionLaunchRoutes();
        app.MapSlackConnectionRoutes();
        app.MapAgentLaunchObservationRoutes();
        app.MapRoutingRulesRoutes();
        app.MapRoutingTestRoutes();
        app.MapAgentSessionListRoutes();
        app.MapAgentSessionContextAssociationRoutes();
        app.MapAgentSessionFollowupRoutes();
        app.MapAgentSessionCancelRoutes();
        app.MapAgentSessionRecoveryRoutes();
        app.MapRunnerRoutes();
        app.MapRunnerStatusRoutes();
        app.MapRunnerIdentityRoutes();
        app.MapWorkflowArtifactUploadRoutes();
        app.MapTaskLogRoutes();
        app.MapHub<RunnerHub>("/hubs/runner");
        app.MapHub<MohistHub>("/hubs/events");
        app.MapOtlpRoutes();
        app.MapOtelQueryRoutes();
        return app;
    }
}
