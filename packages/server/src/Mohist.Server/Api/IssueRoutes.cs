using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Api;

public static class IssueRoutes
{
    private const string ProjectRegistryKey = "project-registry";

    public static WebApplication MapIssueRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues");

        issues.MapGet("/", async (string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var catalog = grains.GetGrain<IIssueCatalogGrain>(pid);
            var list = await catalog.ListAsync();
            return ApiResults.Ok(list);
        });

        issues.MapPost("/", async (CreateIssueRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var catalog = grains.GetGrain<IIssueCatalogGrain>(pid);
            var issue = await catalog.CreateAsync(req.Title, req.Body, req.Labels, req.Priority);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        issues.MapGet("/{number:int}", async (int number, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var info = await grain.GetInfoAsync();
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPatch("/{number:int}", async (int number, UpdateIssueRequest req, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(null, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.UpdateAsync(req.Title, req.Body);
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
                await grain.StartWorkflowAsync();
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
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
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

public record CreateIssueRequest(string Title, string? Body = null, string[]? Labels = null, string? Priority = null);
public record UpdateIssueRequest(string Title, string? Body);
public record RejectRequest(string? Reason);
