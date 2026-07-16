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
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            var result = await grain.AddPrerequisiteAsync(req.PrerequisiteNumber);
            if (!result.Success)
                return ApiResults.NotFound(result.Message);

            var info = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(info);
        });

        group.MapDelete("/{number:int}/prerequisites/{prerequisiteNumber:int}", async (
            HttpContext ctx,
            string projectRef,
            int number,
            int prerequisiteNumber,
            IGrainFactory grains,
            IssueQuerier issuesQuery) =>
        {
            var project = GetRequiredProject(ctx);

            var grain = await GetIssueGrainAsync(grains, issuesQuery, project.Id, number);
            if (grain is null) return ApiResults.NotFound($"Issue #{number} not found");
            await grain.RemovePrerequisiteAsync(prerequisiteNumber);
            var info = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(info);
        });
    }
}
