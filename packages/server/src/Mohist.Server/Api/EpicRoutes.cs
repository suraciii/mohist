using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Queries;
using Mohist.Server.Epics;
using Mohist.Server.Issue.Queries;

namespace Mohist.Server.Api;

public static class EpicRoutes
{
    public static WebApplication MapEpicRoutes(this WebApplication app)
    {
        var epics = app.MapGroup("/api/epics");

        epics.MapGet("/", async (string projectId, EpicQueryService queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await queryService.ListAsync(pid));
        });

        epics.MapPost("/", async (EpicCreateRequest req, string? projectId, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return ApiResults.BadRequest("title is required");
            var pid = projectId ?? req.ProjectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var tempId = $"epic_{Guid.NewGuid():N}";
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{tempId}");
            var dto = await grain.CreateAsync(pid, req.Title, req.Description, req.Priority);
            return Results.Json(new ApiResponse<EpicDto>(true, dto), statusCode: 201);
        });

        epics.MapGet("/{id}", async (string id, string projectId, EpicQueryService queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var result = await queryService.GetAsync(pid, id);
            return result is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(result);
        });

        epics.MapPost("/{id}/issues", async (string id, string projectId, EpicIssueRequest req, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var issues = await issuesQuery.ListAsync(pid, all: true);
            var issue = issues.FirstOrDefault(i => i.Id == req.IssueId || i.Number.ToString() == req.IssueId);
            if (issue is null) return ApiResults.Fail("Issue not found", 404, "ISSUE_NOT_FOUND");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{id}");
            try
            {
                await grain.LinkIssueAsync(issue.Id, issue.Number, pid);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message.Contains("already belongs"))
                    return ApiResults.Conflict(ex.Message, "DUPLICATE_EPIC_MEMBERSHIP");
                throw;
            }
            return ApiResults.Ok(new { epicId = id, issueId = issue.Id });
        });

        epics.MapDelete("/{id}/issues/{issueId}", async (string id, string issueId, string projectId, IGrainFactory grains) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{id}");
            await grain.UnlinkIssueAsync(issueId, pid);
            return ApiResults.Ok(new { epicId = id, issueId });
        });

        epics.MapPost("/{id}/done", async (string id, string projectId, IGrainFactory grains) => await SetStatusRouteAsync(id, projectId, "done", grains));
        epics.MapPost("/{id}/close", async (string id, string projectId, IGrainFactory grains) => await SetStatusRouteAsync(id, projectId, "closed", grains));

        return app;
    }

    private static async Task<IResult> SetStatusRouteAsync(string id, string projectId, string status, IGrainFactory grains)
    {
        var pid = projectId;
        if (pid is null) return ApiResults.BadRequest("No active project");
        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{id}");
        try
        {
            var dto = await grain.SetStatusAsync(status);
            return ApiResults.Ok(dto);
        }
        catch (InvalidOperationException)
        {
            return ApiResults.NotFound($"Epic {id} not found");
        }
    }
}

public record EpicCreateRequest(string Title, string? Description, string? Priority, string? ProjectId = null);
public record EpicIssueRequest(string IssueId);
