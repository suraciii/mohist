namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    public static WebApplication MapIssueRoutes(this WebApplication app)
    {
        var projectIssues = app.MapGroup("/api/projects/{projectRef}/issues")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        projectIssues.MapIssueCrud();
        projectIssues.MapIssuePrerequisites();
        projectIssues.MapIssueLifecycle();
        projectIssues.MapIssueWorkflowControl();
        projectIssues.MapIssueFeedback();
        projectIssues.MapIssueSessions();
        projectIssues.MapIssueWorkflowProfile();
        projectIssues.MapIssueRebase();
        projectIssues.MapIssueWorkflowArtifacts();
        projectIssues.MapIssueWorkflowTaskLogs();
        projectIssues.MapIssueMetrics();
        projectIssues.MapIssueApprovalMetrics();
        projectIssues.MapIssueQualityMetrics();
        projectIssues.MapIssueDeliveryTimeMetrics();
        projectIssues.MapIssueStageDurationMetrics();
        projectIssues.MapIssueWatch();

        return app;
    }
}
