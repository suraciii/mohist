using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mohist.Server.Agent.Services;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static partial class IssueRoutes
{
    internal static void MapIssueWatch(this RouteGroupBuilder group)
    {
        group.MapPost("/{number:int}/watch", async (
            HttpContext ctx,
            string projectRef,
            int number,
            AddWatchRequest req,
            IssueQuerier issuesQuery,
            WatchEntryStore watchStore) =>
        {
            var project = GetRequiredProject(ctx);

            if (req is null || string.IsNullOrWhiteSpace(req.AgentId))
                return ApiResults.BadRequest("agentId is required", "agentId_required");

            var existing = await issuesQuery.GetAsync(project.Id, number);
            if (existing is null)
                return ApiResults.NotFound($"Issue #{number} not found");

            try
            {
                await watchStore.AddAsync(project.Id, number, req.AgentId, ctx.RequestAborted);
            }
            catch (WatchEntryValidationException ex)
            {
                return MapWatchValidationError(ex);
            }

            var info = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(info);
        });

        group.MapDelete("/{number:int}/watch", async (
            HttpContext ctx,
            string projectRef,
            int number,
            [FromBody] DeleteWatchRequest req,
            IssueQuerier issuesQuery,
            WatchEntryStore watchStore) =>
        {
            var project = GetRequiredProject(ctx);

            if (req is null || string.IsNullOrWhiteSpace(req.AgentId))
                return ApiResults.BadRequest("agentId is required", "agentId_required");

            var existing = await issuesQuery.GetAsync(project.Id, number);
            if (existing is null)
                return ApiResults.NotFound($"Issue #{number} not found");

            try
            {
                await watchStore.RemoveAsync(project.Id, number, req.AgentId, ctx.RequestAborted);
            }
            catch (WatchEntryValidationException ex)
            {
                return MapWatchValidationError(ex);
            }

            var info = await issuesQuery.GetAsync(project.Id, number);
            return ApiResults.Ok(info);
        });
    }

    private static IResult MapWatchValidationError(WatchEntryValidationException ex) => ex.Code switch
    {
        "agent_not_found" => ApiResults.Fail(ex.Message, 404, "agent_not_found"),
        "agent_archived" => ApiResults.Fail(ex.Message, 409, "agent_archived"),
        _ => ApiResults.BadRequest(ex.Message, ex.Code),
    };
}

public sealed record AddWatchRequest(string AgentId);

public sealed record DeleteWatchRequest(string AgentId);
