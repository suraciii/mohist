using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWorkflowProfile(this RouteGroupBuilder group)
    {
        group.MapGet("/{number:int}/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            IssueVariableStore variableStore,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            return ApiResults.Ok(await variableStore.GetVariablesAsync(project.Id, number));
        });

        group.MapPut("/{number:int}/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            VariableBundle bundle,
            IssueVariableStore variableStore,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                return ApiResults.Ok(await variableStore.SetVariablesAsync(project.Id, number, bundle));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_agent_config");
            }
        });

        group.MapPatch("/{number:int}/variables", async (
            HttpContext ctx,
            string projectRef,
            int number,
            VariableBundle patch,
            IssueVariableStore variableStore,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);
            var issue = await issuesQuery.GetInfoAsync(project.Id, number, project);
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                return ApiResults.Ok(await variableStore.PatchVariablesAsync(project.Id, number, patch));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message, "invalid_agent_config");
            }
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
