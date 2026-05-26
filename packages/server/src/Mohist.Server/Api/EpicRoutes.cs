using Microsoft.EntityFrameworkCore;
using Mohist.Server.Epics;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Grains;
using Mohist.Server.Storage.Db;

namespace Mohist.Server.Api;

public static class EpicRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapEpicRoutes(this WebApplication app)
    {
        var epics = app.MapGroup("/api/epics");

        epics.MapGet("/", async (string? projectId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await db.Epics.AsNoTracking()
                .Where(e => e.ProjectId == pid)
                .ToListAsync();
            rows = rows.OrderBy(e => e.CreatedAt).ToList();
            var result = new List<EpicWithProgressDto>();
            foreach (var epic in rows)
                result.Add(await ToWithProgressAsync(db, issuesQuery, epic));
            return ApiResults.Ok(result);
        });

        epics.MapPost("/", async (EpicCreateRequest req, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title)) return ApiResults.BadRequest("title is required");
            var pid = await ResolveProjectIdAsync(req.ProjectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            await using var db = await dbFactory.CreateDbContextAsync();
            var now = DateTimeOffset.UtcNow;
            var epic = new EpicEntry
            {
                Id = $"epic_{Guid.NewGuid():N}",
                ProjectId = pid,
                Title = req.Title,
                Description = req.Description ?? "",
                Priority = string.IsNullOrWhiteSpace(req.Priority) ? "p2" : req.Priority,
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Epics.Add(epic);
            await db.SaveChangesAsync();
            return Results.Json(new ApiResponse<EpicDto>(true, ToDto(epic)), statusCode: 201);
        });

        epics.MapGet("/{id}", async (string id, string? projectId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            await using var db = await dbFactory.CreateDbContextAsync();
            var epic = await db.Epics.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == pid && e.Id == id);
            return epic is null ? ApiResults.NotFound($"Epic {id} not found") : ApiResults.Ok(await ToDetailAsync(db, issuesQuery, epic));
        });

        epics.MapPost("/{id}/issues", async (string id, string? projectId, EpicIssueRequest req, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            await using var db = await dbFactory.CreateDbContextAsync();

            var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == pid && e.Id == id);
            if (epic is null) return ApiResults.NotFound($"Epic {id} not found");

            var issues = await issuesQuery.ListAsync(pid, all: true);
            var issue = issues.FirstOrDefault(i => i.Id == req.IssueId || i.Number.ToString() == req.IssueId);
            if (issue is null) return ApiResults.Fail("Issue not found", 404, "ISSUE_NOT_FOUND");

            var existing = await db.EpicIssues.AsNoTracking().FirstOrDefaultAsync(link => link.ProjectId == pid && link.IssueId == issue.Id);
            if (existing is not null && existing.EpicId != id)
            {
                var existingEpic = await db.Epics.AsNoTracking().FirstOrDefaultAsync(e => e.ProjectId == pid && e.Id == existing.EpicId);
                return ApiResults.Conflict(
                    "Issue already belongs to another Epic",
                    "DUPLICATE_EPIC_MEMBERSHIP",
                    new { existingEpicId = existing.EpicId, existingEpicTitle = existingEpic?.Title });
            }

            if (existing is null)
            {
                db.EpicIssues.Add(new EpicIssueEntry { EpicId = id, ProjectId = pid, IssueId = issue.Id, IssueNumber = issue.Number });
                epic.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }

            return ApiResults.Ok(new { epicId = id, issueId = issue.Id });
        });

        epics.MapDelete("/{id}/issues/{issueId}", async (string id, string issueId, string? projectId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            await using var db = await dbFactory.CreateDbContextAsync();
            var row = await db.EpicIssues.FirstOrDefaultAsync(link => link.ProjectId == pid && link.EpicId == id && link.IssueId == issueId);
            if (row is not null)
            {
                db.EpicIssues.Remove(row);
                var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == pid && e.Id == id);
                if (epic is not null) epic.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
            return ApiResults.Ok(new { epicId = id, issueId });
        });

        epics.MapPost("/{id}/done", async (string id, string? projectId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory) => await SetStatusAsync(id, projectId, "done", grains, dbFactory));
        epics.MapPost("/{id}/close", async (string id, string? projectId, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory) => await SetStatusAsync(id, projectId, "closed", grains, dbFactory));

        return app;
    }

    private static async Task<IResult> SetStatusAsync(string id, string? projectId, string status, IGrainFactory grains, IDbContextFactory<MohistDbContext> dbFactory)
    {
        var pid = await ResolveProjectIdAsync(projectId, grains);
        if (pid is null) return ApiResults.BadRequest("No active project");
        await using var db = await dbFactory.CreateDbContextAsync();
        var epic = await db.Epics.FirstOrDefaultAsync(e => e.ProjectId == pid && e.Id == id);
        if (epic is null) return ApiResults.NotFound($"Epic {id} not found");
        epic.Status = status;
        epic.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return ApiResults.Ok(ToDto(epic));
    }

    private static async Task<EpicWithProgressDto> ToWithProgressAsync(MohistDbContext db, IssueQueryService issuesQuery, EpicEntry epic)
    {
        var progress = await BuildProgressAsync(db, issuesQuery, epic);
        return new EpicWithProgressDto(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), progress);
    }

    private static async Task<EpicDetailDto> ToDetailAsync(MohistDbContext db, IssueQueryService issuesQuery, EpicEntry epic)
    {
        var linked = await GetLinkedIssuesAsync(db, issuesQuery, epic);
        var progress = BuildProgress(linked);
        return new EpicDetailDto(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"), linked, progress);
    }

    private static EpicDto ToDto(EpicEntry epic) => new(epic.Id, epic.Title, epic.Description, epic.Priority, epic.Status, epic.CreatedAt.ToString("o"), epic.UpdatedAt.ToString("o"));

    private static async Task<EpicProgressDto> BuildProgressAsync(MohistDbContext db, IssueQueryService issuesQuery, EpicEntry epic) =>
        BuildProgress(await GetLinkedIssuesAsync(db, issuesQuery, epic));

    private static async Task<List<LinkedIssueDto>> GetLinkedIssuesAsync(MohistDbContext db, IssueQueryService issuesQuery, EpicEntry epic)
    {
        var links = await db.EpicIssues.AsNoTracking()
            .Where(link => link.ProjectId == epic.ProjectId && link.EpicId == epic.Id)
            .ToListAsync();
        links = links.OrderBy(link => link.CreatedAt).ToList();
        if (links.Count == 0) return [];

        var allIssues = await issuesQuery.ListAsync(epic.ProjectId, all: true);
        var byId = allIssues.ToDictionary(i => i.Id);
        return links
            .Select(link => byId.TryGetValue(link.IssueId, out var issue) ? new LinkedIssueDto(issue.Id, issue.Number, issue.Title, issue.RuntimeStatus, issue.Stage, issue.Priority) : null)
            .Where(i => i is not null)
            .Cast<LinkedIssueDto>()
            .ToList();
    }

    private static EpicProgressDto BuildProgress(IReadOnlyList<LinkedIssueDto> linked)
    {
        var completed = linked.Where(IsCompleted).ToList();
        var next = linked.FirstOrDefault(i => !IsCompleted(i));
        return new EpicProgressDto(
            completed.Count,
            linked.Count,
            linked.Where(i => i.Status == "blocked").Select(i => i.Id).ToArray(),
            linked.Where(i => i.Status == "active" && !IsCompleted(i)).Select(i => i.Id).ToArray(),
            next is null ? null : new EpicNextIssueDto(next.Id, next.Number, next.Title),
            linked.Count > 0 && completed.Count == linked.Count);
    }

    private static bool IsCompleted(LinkedIssueDto issue) => issue.Stage == "done" || issue.Status == "completed";

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, IGrainFactory grains)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }
}

public record EpicCreateRequest(string Title, string? Description, string? Priority, string? ProjectId = null);
public record EpicIssueRequest(string IssueId);
