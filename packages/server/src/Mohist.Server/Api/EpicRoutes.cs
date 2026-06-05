using Mohist.Server.Epic.Grains;
using Mohist.Server.Epic.Services;
using Mohist.Server.Issue.Services;
using Mohist.Server.Project.Services;

namespace Mohist.Server.Api;

public static class EpicRoutes
{
    public static WebApplication MapEpicRoutes(this WebApplication app)
    {
        var epics = app.MapGroup("/api/projects/{projectRef}/epics");

        epics.MapGet("/", async (string projectRef, EpicQuerier queryService, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            return ApiResults.Ok(await queryService.ListAsync(project.Id));
        });

        epics.MapPost("/", async (string projectRef, EpicCreateRequest req, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return ApiResults.BadRequest("title is required");
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            var pid = project.Id;

            var tempId = $"epic_{Guid.NewGuid():N}";
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{tempId}");
            var dto = await grain.CreateAsync(pid, req.Title, req.Description, req.Priority);
            return Results.Json(new ApiResponse<EpicDto>(true, dto), statusCode: 201);
        });

        epics.MapGet("/{id}", async (string projectRef, string id, EpicQuerier queryService, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            var pid = project.Id;
            var result = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            return result is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(result);
        });

        epics.MapPatch("/{id}", async (string projectRef, string id, UpdateEpicRequest req, EpicQuerier queryService, IGrainFactory grains, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            var pid = project.Id;

            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");

            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            var updated = await grain.UpdateAsync(req.Title, req.Description, req.Priority);
            return updated is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(updated);
        });

        epics.MapPost("/{id}/issues", async (string projectRef, string id, EpicIssueRequest req, IGrainFactory grains, IssueQuerier issuesQuery, EpicQuerier queryService, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            var pid = project.Id;

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

        epics.MapDelete("/{id}/issues/{issueId}", async (string projectRef, string id, string issueId, IGrainFactory grains, EpicQuerier queryService, ProjectRefResolver projects) =>
        {
            var project = await projects.ResolveAsync(projectRef);
            if (project is null) return ApiResults.NotFound("Project not found");
            var pid = project.Id;
            var resolved = int.TryParse(id, out var number)
                ? await queryService.GetByNumberAsync(pid, number)
                : await queryService.GetAsync(pid, id);
            if (resolved is null) return ApiResults.NotFound($"Epic {id} not found");
            var grain = grains.GetGrain<IEpicGrain>($"{pid}:{resolved.Id}");
            await grain.UnlinkIssueAsync(issueId, pid);
            return ApiResults.Ok(new { epicId = resolved.Id, issueId });
        });

        epics.MapPost("/{id}/done", async (string projectRef, string id, IGrainFactory grains, EpicQuerier queryService, ProjectRefResolver projects) => await SetStatusRouteAsync(projectRef, id, "done", grains, queryService, projects));
        epics.MapPost("/{id}/close", async (string projectRef, string id, IGrainFactory grains, EpicQuerier queryService, ProjectRefResolver projects) => await SetStatusRouteAsync(projectRef, id, "closed", grains, queryService, projects));

        return app;
    }

    private static async Task<IResult> SetStatusRouteAsync(string projectRef, string id, string status, IGrainFactory grains, EpicQuerier queryService, ProjectRefResolver projects)
    {
        var project = await projects.ResolveAsync(projectRef);
        if (project is null) return ApiResults.NotFound("Project not found");
        var pid = project.Id;
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

public record EpicCreateRequest(string Title, string? Description, string? Priority);
public record EpicIssueRequest(string IssueId);
public record UpdateEpicRequest(string? Title = null, string? Description = null, string? Priority = null);
