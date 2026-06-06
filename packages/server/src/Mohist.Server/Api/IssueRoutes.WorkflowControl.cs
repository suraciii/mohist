using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWorkflowControl(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/resume", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/approve", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/reject", async (
            HttpContext ctx,
            string projectRef,
            int number,
            RejectRequest? req,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/retry", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        group.MapPost("/{number:int}/rerun", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        // Force-stop is implemented as workflow pause. The user can resume afterwards.
        // For terminal disposal, use /close (issue close -> workflow Stopped) or /stop.
        group.MapPost("/{number:int}/force-stop", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        // Stop is a terminal pause: the workflow run is permanently stopped (cannot be resumed).
        // The issue itself is NOT closed; the user can re-open or close it separately.
        group.MapPost("/{number:int}/stop", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery,
            ProjectRefResolver projects) =>
        {
            var project = GetRequiredProject(ctx, projects, projectRef);
            var wrId = (await issuesQuery.GetInfoAsync(project.Id, number))?.WorkflowRunId;
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            try
            {
                await grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("user-stop");
                return ApiResults.Ok();
            }
            catch (WorkflowDomainException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });
    }
}
