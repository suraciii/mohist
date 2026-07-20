using Microsoft.AspNetCore.Routing;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWorkflowProfile(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/workflow-profile", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery,
            ProjectQuerier projectsQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var response = await BuildIssueWorkflowProfileResponseAsync(project.Id, number, issueProfileManager, issuesQuery, projectsQuery);
            return response is null ? ApiResults.NotFound($"Issue #{number} not found") : ApiResults.Ok(response);
        });

        group.MapPut("/{number:int}/workflow-profile/template", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueTemplateRequest req,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery,
            ProjectQuerier projectsQuery) =>
        {
            var project = GetRequiredProject(ctx);
            return await UpdateIssueWorkflowTemplateAsync(project.Id, number, req, issueProfileManager, issuesQuery, projectsQuery);
        });

        group.MapDelete("/{number:int}/workflow-profile/template", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery,
            ProjectQuerier projectsQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            await issueProfileManager.UpdateTemplateAsync(project.Id, number, new IssueTemplateUpdateRequest());
            var response = await BuildIssueWorkflowProfileResponseAsync(project.Id, number, issueProfileManager, issuesQuery, projectsQuery);
            return ApiResults.Ok(response!);
        });

        group.MapGet("/{number:int}/workflow-profile/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.GetVariablesAsync(project.Id, number));
        });

        group.MapPut("/{number:int}/workflow-profile/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            VariableBundle bundle,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.SetVariablesAsync(project.Id, number, bundle));
        });

        group.MapPatch("/{number:int}/workflow-profile/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            VariableBundle patch,
            IssueWorkflowProfileManager issueProfileManager,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.PatchVariablesAsync(project.Id, number, patch));
        });

        group.MapGet("/{number:int}/workflow/status", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                var status = await grain.GetWorkflowStatusAsync();
                return status is not null ? ApiResults.Ok(status) : ApiResults.NotFound("Workflow not found");
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });
    }
}
