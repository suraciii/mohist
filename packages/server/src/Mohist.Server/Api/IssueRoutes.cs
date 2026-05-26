using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Grains;
using Mohist.Server.Sessions;
using Mohist.Server.Workspace;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Projection;
using System.Text.Json;

namespace Mohist.Server.Api;

public static class IssueRoutes
{
    private const string ProjectKey = "projects";

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

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            var list = await issuesQuery.ListAsync(pid, project, stage, label, priority, archived, all);

            return ApiResults.Ok(list);
        });

        issues.MapPost("/", async (CreateIssueRequest req, IGrainFactory grains) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var pid = await ResolveProjectIdAsync(req.ProjectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            if (await projectsGrain.GetByIdAsync(pid) is null) return ApiResults.NotFound("Project not found");

            var counter = grains.GetGrain<IIssueCounterGrain>(pid);
            var number = await counter.NextAsync();
            var issueGrain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            await issueGrain.HydrateAsync(pid, number, req.Title, req.Body, req.Labels, req.Priority, req.Model, req.StageModels, req.WorkflowProfileId);
            var issue = await issueGrain.GetInfoAsync();
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        issues.MapGet("/{number:int}", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            var info = await issuesQuery.GetAsync(pid, number, project);
            return info is not null ? ApiResults.Ok(info) : ApiResults.NotFound($"Issue #{number} not found");
        });

        issues.MapPatch("/{number:int}", async (int number, string? projectId, UpdateIssueRequest req, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        issues.MapPost("/{number:int}/start", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var eligibility = await grain.GetStartEligibilityAsync();
                if (!eligibility.Startable)
                    return ApiResults.Conflict(eligibility.Message ?? "Issue is waiting for prerequisites", "start_blocked", eligibility);

                var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
                var project = await projectsGrain.GetByIdAsync(pid);
                if (project is null) return ApiResults.BadRequest("No active project");

                await grain.StartWorkflowAsync(new WorkflowProjectContext(project.Id, project.Name, project.Path, project.BaseBranch));
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/close", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        issues.MapPost("/{number:int}/reopen", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        issues.MapPost("/{number:int}/archive", async (int number, string? projectId, IGrainFactory grains, IGitService git) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.ArchiveAsync();
                var cleanup = await git.RemoveWorktreeAsync(project.Path, project.Name, number);
                return ApiResults.Ok(new
                {
                    message = cleanup.Removed
                        ? "Issue archived and worktree removed"
                        : "Issue archived",
                    cleanup,
                    warning = cleanup.Status == "failed" ? cleanup.Message : null,
                });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/unarchive", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        issues.MapPost("/archive-completed", async (string? projectId, IGrainFactory grains, IssueQueryService issuesQuery, IGitService git) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var all = await issuesQuery.ListAsync(pid, project, all: true);
            var completed = all.Where(i => i.Stage == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Stage != "done" && i.ArchivedAt == null).ToList();
            var cleanupFailed = 0;

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>($"{pid}:{issue.Number}");
                try
                {
                    await grain.ArchiveAsync();
                    var cleanup = await git.RemoveWorktreeAsync(project.Path, project.Name, issue.Number);
                    if (cleanup.Status == "failed") cleanupFailed++;
                }
                catch
                {
                    /* skip if already archived */
                }
            }

            return ApiResults.Ok(new
            {
                archived = completed.Count,
                skipped = skipped.Count,
                skippedNumbers = skipped.Select(s => s.Number).ToList(),
                cleanupFailed,
                message = $"Archived {completed.Count} completed issues, skipped {skipped.Count}"
            });
        });

        issues.MapGet("/{number:int}/workflow/status", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
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

        issues.MapGet("/{number:int}/workflow/timeline", async (int number, string? projectId, IGrainFactory grains, WorkflowProjectionService projection) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var timeline = await projection.GetTimelineAsync(pid, number);
            return timeline is not null ? ApiResults.Ok(timeline) : ApiResults.NotFound("Workflow not found");
        });

        issues.MapGet("/{number:int}/coder-sessions", async (int number, string? projectId, IGrainFactory grains, AgentSessionService sessions) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await sessions.ListByIssueAsync(pid, number));
        });

        issues.MapGet("/{number:int}/coder-sessions/{sessionId}", async (int number, string sessionId, string? projectId, IGrainFactory grains, AgentSessionService sessions) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var detail = await sessions.GetDetailAsync(pid, number, sessionId);
            return detail is null ? ApiResults.NotFound($"Coder session {sessionId} not found") : ApiResults.Ok(detail);
        });

        issues.MapPost("/{number:int}/resume", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rebase", async (int number, string? projectId, RebaseRequest? req, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var workflow = grains.GetGrain<IWorkflowGrain>(wrId);
            if (await workflow.HasIncompleteTaskUsingAsync("mohist/rebase")
                || await workflow.HasIncompleteTaskIdAsync("resolve-rebase-conflicts")
                || await workflow.HasIncompleteTaskIdAsync("verify-rebase"))
                return ApiResults.Conflict("Rebase task is already pending", "rebase_already_pending");

            var baseBranch = string.IsNullOrWhiteSpace(req?.BaseBranch) ? project.BaseBranch : req!.BaseBranch!;
            var taskId = $"rebase-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var task = new RuntimeTaskInput(
                taskId,
                $"Rebase onto {baseBranch}",
                "mohist/rebase",
                BuildRebaseTaskWith(baseBranch, req?.ConflictResolver),
                InvalidateChecks: true);

            var added = await workflow.AddTaskAsync(task);
            return ApiResults.Ok(new
            {
                rebased = false,
                status = "queued",
                message = "Rebase task queued",
                workflowRunId = wrId,
                taskId = added.TaskId,
                stage = added.Stage,
                baseBranch,
            });
        });

        issues.MapPost("/{number:int}/approve", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/reject", async (int number, string? projectId, RejectRequest? req, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/retry", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rerun", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/force-stop", async (int number, string? projectId, IGrainFactory grains) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains);
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

        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }

    private static async Task<(string? pid, string? wrId)> ResolveWorkflowRunIdAsync(int number, string? projectId, IGrainFactory grains)
    {
        var pid = await ResolveProjectIdAsync(projectId, grains);
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

    private static string BuildRebaseTaskWith(string baseBranch, RuntimeTaskRequest? conflictResolver)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
        };

        var resolver = conflictResolver ?? DefaultConflictResolver();
        if (resolver is not null)
        {
            with["conflictResolver"] = new
            {
                id = string.IsNullOrWhiteSpace(resolver.Id) ? "resolve-rebase-conflicts" : resolver.Id,
                title = string.IsNullOrWhiteSpace(resolver.Title) ? "Resolve rebase conflicts" : resolver.Title,
                uses = string.IsNullOrWhiteSpace(resolver.Uses) ? "mohist/acp-agent" : resolver.Uses,
                with = resolver.With ?? DefaultConflictResolverWith(),
            };
        }

        return JsonSerializer.Serialize(with, WorkflowVariableJson.Options);
    }

    private static RuntimeTaskRequest DefaultConflictResolver() => new(
        Id: "resolve-rebase-conflicts",
        Title: "Resolve rebase conflicts",
        Uses: "mohist/acp-agent",
        With: DefaultConflictResolverWith());

    private static Dictionary<string, object?> DefaultConflictResolverWith() => new()
    {
        ["stage"] = "maintenance",
        ["task"] = "resolve-rebase-conflicts",
        ["description"] = "Resolve git rebase conflicts, stage resolved files, and continue the rebase until it completes.",
    };
}

public record CreateIssueRequest(
    string Title,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, string>? StageModels = null,
    string? WorkflowProfileId = null,
    string? ProjectId = null);

public record UpdateIssueRequest(
    string? Title = null,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, string>? StageModels = null);

public record RejectRequest(string? Reason);

public sealed record RebaseRequest(string? BaseBranch = null, RuntimeTaskRequest? ConflictResolver = null);

public sealed record RuntimeTaskRequest(
    string? Id = null,
    string? Title = null,
    string? Uses = null,
    Dictionary<string, object?>? With = null);
