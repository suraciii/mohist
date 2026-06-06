using Microsoft.AspNetCore.Routing;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssuePrerequisites(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/prerequisites", async (
            HttpContext ctx,
            string projectRef,
            int number,
            AddPrerequisiteRequest req,
            IGrainFactory grains,
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                var result = await grain.AddPrerequisiteAsync(req.PrerequisiteNumber);
                if (!result.Success)
                    return ApiResults.NotFound(result.Message);

                var info = await issuesQuery.GetAsync(project.Id, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        group.MapDelete("/{number:int}/prerequisites/{prerequisiteNumber:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            int prerequisiteNumber,
            IGrainFactory grains,
            IssueIdentityResolver issueIdentityResolver,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issueIdentityResolver, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            try
            {
                await grain.RemovePrerequisiteAsync(prerequisiteNumber);
                var info = await issuesQuery.GetAsync(project.Id, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });
    }
}
