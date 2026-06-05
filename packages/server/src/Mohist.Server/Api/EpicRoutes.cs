using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;

namespace Mohist.Server.Api;

public static class EpicRoutes
{
    public static WebApplication MapEpicRoutes(this WebApplication app)
    {
        var epics = app.MapGroup("/api/epics");

        epics.MapGet("/", async (string projectId, EpicQuerier queryService) =>
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

        epics.MapGet("/by-number/{number:int}", async (int number, string projectId, EpicQuerier queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var result = await queryService.GetByNumberAsync(pid, number);
            return result is null ? ApiResults.NotFound($"Epic #{number} not found") : ApiResults.Ok(result);
        });

        epics.MapGet("/{id}", async (string id, string projectId, EpicQuerier queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var result = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            return result is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(result);
        });

        epics.MapPatch("/{id}", async (string id, string projectId, UpdateEpicRequest req, EpicQuerier queryService, IGrainFactory grains) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            var updated = await grain.UpdateAsync(req.Title, req.Description, req.Priority);
            return updated is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(updated);
        });

        epics.MapPost("/{id}/issues", async (string id, string projectId, EpicIssueRequest req, IGrainFactory grains, IssueQuerier issuesQuery, EpicQuerier queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");
            var resolvedId = resolved.Id;

            var issues = await issuesQuery.ListAsync(pid, all: true);
            var issue = issues.FirstOrDefault(i => i.Id == req.IssueId || i.Number.ToString() == req.IssueId);
            if (issue is null) return ApiResults.Fail("Issue not found", 404, "ISSUE_NOT_FOUND");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolvedId}");
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
            return ApiResults.Ok(new { epicId = resolvedId, issueId = issue.Id });
        });

        epics.MapDelete("/{id}/issues/{issueId}", async (string id, string issueId, string projectId, IGrainFactory grains, EpicQuerier queryService) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            await grain.UnlinkIssueAsync(issueId, pid);
            return ApiResults.Ok(new { epicId = resolved.Id, issueId });
        });

        epics.MapPost("/{id}/done", async (string id, string projectId, IGrainFactory grains, EpicQuerier queryService) => await SetStatusRouteAsync(id, projectId, "done", grains, queryService));
        epics.MapPost("/{id}/close", async (string id, string projectId, IGrainFactory grains, EpicQuerier queryService) => await SetStatusRouteAsync(id, projectId, "closed", grains, queryService));

        return app;
    }

    private static async Task<IResult> SetStatusRouteAsync(string id, string projectId, string status, IGrainFactory grains, EpicQuerier queryService)
    {
        var pid = projectId;
        if (pid is null) return ApiResults.BadRequest("No active project");
        var resolved = int.TryParse(id, out var number)
            ? await queryService.GetByNumberAsync(pid, number)
            : await queryService.GetAsync(pid, id);
        if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

        var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
        try
        {
            var dto = await grain.SetStatusAsync(status);
            return ApiResults.Ok(dto);
        }
        catch (EpicNotReadyToMarkDoneException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_NOT_READY_TO_MARK_DONE", new { undeliveredCount = ex.UndeliveredCount });
        }
        catch (EpicAlreadyTerminalException ex)
        {
            return ApiResults.Conflict(ex.Message, "EPIC_ALREADY_TERMINAL", new { currentStatus = ex.CurrentStatus, requestedStatus = ex.RequestedStatus });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return ApiResults.NotFound(ex.Message);
        }
    }
}

public record EpicCreateRequest(string Title, string? Description, string? Priority, string? ProjectId = null);
public record EpicIssueRequest(string IssueId);
public record UpdateEpicRequest(string? Title = null, string? Description = null, string? Priority = null);
