using Mohist.Server.Api;
using Mohist.Server.Auth.Domain;
using Mohist.Server.Auth.Identity;
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
        app.MapAuthSessionRoutes();
        app.MapAuthDeviceRoutes();
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
        app.MapWorkspaceEntityRoutes();
        app.MapEpicRoutes();
        app.MapInboxRoutes();
        app.MapAgentDefinitionRoutes();
        app.MapAgentRoutes();
        app.MapAgentJobRoutes();
        app.MapAgentJobReadRoutes();
        app.MapAgentSessionLaunchRoutes();
        app.MapAgentSessionTreeRoutes();
        app.MapAgentSessionSpawnRoutes();
        app.MapSlackConnectionRoutes();
        app.MapSlackInteractionRoutes();
        app.MapSlackManagerRoutes();
        app.MapSlackManagerIngressRoutes();
        app.MapSlackAdapterLeaseRoutes();
        app.MapAgentLaunchObservationRoutes();
        app.MapRoutingRulesRoutes();
        app.MapWebhookSubscriptionsRoutes();
        app.MapGitHubConnectionRoutes();
        app.MapGitHubIngressRoutes();
        app.MapAuthTokenRoutes();
        app.MapRunnerEnrollmentRoutes();
        app.MapIntegrationTokenRoutes();
        app.MapAuditRoutes();
        app.MapRoutingTestRoutes();
        app.MapAgentSessionListRoutes();
        app.MapAgentSessionContextAssociationRoutes();
        app.MapAgentSessionFollowupRoutes();
        app.MapAgentSessionScheduleRoutes();
        app.MapAgentSessionCancelRoutes();
        app.MapAgentSessionStopRoutes();
        app.MapAgentSessionStopOperationReadRoute();
        app.MapAgentSessionDetachRoutes();
        app.MapAgentSessionRecoveryRoutes();
        app.MapRunnerRoutes();
        app.MapRunnerStatusRoutes();
        app.MapRunnerIdentityRoutes();
        app.MapWorkflowArtifactUploadRoutes();
        app.MapTaskLogRoutes();
        app.MapHub<RunnerHub>("/hubs/runner").RequireScopes(Scope.Runner);
        app.MapHub<MohistHub>("/hubs/events").RequireScopes(Scope.Operator, Scope.Readonly);
        app.MapOtlpRoutes();
        app.MapOtelQueryRoutes();
        return app;
    }
}
