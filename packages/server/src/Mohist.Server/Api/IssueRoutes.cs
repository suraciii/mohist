using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static class IssueRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapIssueRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues");

        issues.MapGet("/", async (
            string? projectId,
            string? stage,
            string? label,
            string? priority,
            bool? archived,
            bool? all,
            IGrainFactory grains,
            IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByNameAsync(pid) ?? await registry.GetCurrentAsync();
            var list = await issuesQuery.ListAsync(pid, project, stage, label, priority, archived, all);

            return ApiResults.Ok(list);
        });

        issues.MapPost("/", async (CreateIssueRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var catalog = grains.GetGrain<IIssueCatalogGrain>(pid);
            var issue = await catalog.CreateAsync(req.Title, req.Body, req.Labels, req.Priority, req.Model, req.StageModels);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        issues.MapGet("/{number:int}", async (int number, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
            var project = await registry.GetByNameAsync(pid) ?? await registry.GetCurrentAsync();
            var info = await issuesQuery.GetAsync(pid, number, project);
            return info is not null ? ApiResults.Ok(info) : ApiResults.NotFound($"Issue #{number} not found");
        });

        issues.MapPatch("/{number:int}", async (int number, UpdateIssueRequest req, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    req.Title, req.Body, req.Labels, req.Priority, req.Model, req.StageModels));
                var info = await grain.GetInfoAsync();
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/start", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
                var project = await registry.GetCurrentAsync();
                if (project is null) return ApiResults.BadRequest("No active project");

                await grain.StartWorkflowAsync(new WorkflowProjectContext(project.Id, project.Name, project.Path, project.BaseBranch));
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/close", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.CloseAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/reopen", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.ReopenAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/archive", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.ArchiveAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/unarchive", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.UnarchiveAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/archive-completed", async (IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var all = await issuesQuery.ListAsync(pid, all: true);
            var completed = all.Where(i => i.Stage == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Stage != "done" && i.ArchivedAt == null).ToList();

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>($"{pid}:{issue.Number}");
                try { await grain.ArchiveAsync(); } catch { /* skip if already archived */ }
            }

            return ApiResults.Ok(new
            {
                archived = completed.Count,
                skipped = skipped.Count,
                skippedNumbers = skipped.Select(s => s.Number).ToList(),
                message = $"Archived {completed.Count} completed issues, skipped {skipped.Count}"
            });
        });

        issues.MapGet("/{number:int}/workflow/status", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
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

        issues.MapPost("/{number:int}/resume", async (int number, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/approve", async (int number, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/reject", async (int number, RejectRequest? req, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/retry", async (int number, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rerun", async (int number, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/force-stop", async (int number, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        return app;
    }

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, IGrainFactory grains)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;

        var registry = grains.GetGrain<IProjectRegistryGrain>(ProjectRegistryKey);
        var current = await registry.GetCurrentAsync();
        return current?.Id;
    }

    private static async Task<(string? pid, string? wrId)> ResolveWorkflowRunIdAsync(int number, IGrainFactory grains)
    {
        var pid = await ResolveProjectIdAsync(null, grains);
        if (pid is null) return (null, null);

        var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
        try
        {
            var wrId = await grain.GetWorkflowRunIdAsync();
            return (pid, wrId);
        }
        catch (InvalidOperationException)
        {
            return (pid, null);
        }
    }
}

public record CreateIssueRequest(
    string Title,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, string>? StageModels = null);

public record UpdateIssueRequest(
    string? Title = null,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, string>? StageModels = null);

public record RejectRequest(string? Reason);
