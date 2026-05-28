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
            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            if (all == true && string.IsNullOrWhiteSpace(projectId))
            {
                var projects = await projectsGrain.GetAllAsync();
                var allIssues = new List<IssueReadModel>();
                foreach (var listedProject in projects)
                    allIssues.AddRange(await issuesQuery.ListAsync(listedProject.Id, listedProject, stage, label, priority, archived, all));

                return ApiResults.Ok(allIssues.OrderBy(i => i.Number).ToList());
            }

            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            var list = await issuesQuery.ListAsync(pid, project, stage, label, priority, archived, all);

            return ApiResults.Ok(list);
        });

        issues.MapPost("/", async (CreateIssueRequest req, IGrainFactory grains, IssueQueryService issuesQuery) =>
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
            await issueGrain.HydrateAsync(pid, number, req.Title, req.Body, req.Labels, req.Priority, req.Model, req.AgentConfig, req.StageModels, req.WorkflowProfileId);
            var issue = await issuesQuery.GetAsync(pid, number);
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

        issues.MapPatch("/{number:int}", async (int number, string? projectId, UpdateIssueRequest req, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    req.Title, req.Body, req.Labels, req.Priority, req.Model, req.AgentConfig, req.StageModels, req.StageVariables));
                var info = await issuesQuery.GetAsync(pid, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/start", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
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

        issues.MapPost("/{number:int}/close", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
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

        issues.MapPost("/{number:int}/reopen", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
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

        issues.MapPost("/{number:int}/unarchive", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
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

        issues.MapGet("/{number:int}/workflow/status", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
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

        issues.MapPost("/{number:int}/resume", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rebase", async (int number, string? projectId, RebaseRequest? req, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
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

        issues.MapPost("/{number:int}/approve", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/reject", async (int number, string? projectId, RejectRequest? req, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/retry", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rerun", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/force-stop", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        // Stage-level variable overrides
        issues.MapGet("/{number:int}/stage-variables", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");
            var info = await issuesQuery.GetInfoAsync(pid, number);
            if (info is null) return ApiResults.NotFound($"Issue #{number} not found");
            return ApiResults.Ok(new { stageVariables = info.StageVariables });
        });

        issues.MapPut("/{number:int}/stage-variables/{stage}", async (int number, string stage, SetStageVariablesRequest req, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var info = await issuesQuery.GetInfoAsync(pid, number);
                if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

                var stageVars = info.StageVariables ?? new Dictionary<string, Dictionary<string, string>>();
                if (!stageVars.TryGetValue(stage, out var vars))
                {
                    vars = new Dictionary<string, string>();
                    stageVars[stage] = vars;
                }

                foreach (var (key, value) in req.Variables)
                {
                    vars[key] = value;
                }

                await grain.UpdateFullAsync(new UpdateIssueData(StageVariables: stageVars));
                return ApiResults.Ok(new { stage, variables = vars });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapDelete("/{number:int}/stage-variables/{stage}", async (int number, string stage, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                var info = await issuesQuery.GetInfoAsync(pid, number);
                if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

                var stageVars = info.StageVariables ?? new Dictionary<string, Dictionary<string, string>>();
                stageVars.Remove(stage);

                await grain.UpdateFullAsync(new UpdateIssueData(StageVariables: stageVars));
                return ApiResults.Ok(new { stage, removed = true });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        // Issue-scoped active workflow definition vars. Mohist does not expose workflow runs as standalone product resources.
        issues.MapGet("/{number:int}/workflow/yaml", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var yaml = await grains.GetGrain<IWorkflowGrain>(wrId).GetDefinitionYamlAsync();
            return yaml is null
                ? ApiResults.NotFound("Workflow definition not found")
                : ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, yaml });
        });

        issues.MapGet("/{number:int}/workflow/vars", async (int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await grains.GetGrain<IWorkflowGrain>(wrId).GetVariablesAsync();
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, vars = SectionValue(snapshot?.Variables, "vars") });
        });

        issues.MapGet("/{number:int}/workflow/vars/{name}", async (int number, string name, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await grains.GetGrain<IWorkflowGrain>(wrId).GetVariablesAsync();
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, name, value = VarValue(snapshot?.Variables, name) });
        });

        issues.MapPatch("/{number:int}/workflow/vars/{name}", async (int number, string name, JsonElement patch, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await grains.GetGrain<IWorkflowGrain>(wrId).PatchVariablesAsync("vars", JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [name] = patch }));
            return ApiResults.Ok(WorkflowVarsResponse(number, wrId, snapshot));
        });

        issues.MapGet("/{number:int}/workflow/stages/{stage}/vars/{name}", async (int number, string stage, string name, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await grains.GetGrain<IWorkflowGrain>(wrId).GetVariablesAsync();
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, stage, name, value = StageVarValue(snapshot?.StageVariables, stage, name) });
        });

        issues.MapPatch("/{number:int}/workflow/stages/{stage}/vars/{name}", async (int number, string stage, string name, JsonElement patch, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery) =>
        {
            var (pid, wrId) = await ResolveWorkflowRunIdAsync(number, projectId, grains, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await grains.GetGrain<IWorkflowGrain>(wrId).PatchStageVariablesAsync(stage, "vars", JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [name] = patch }));
            return ApiResults.Ok(WorkflowVarsResponse(number, wrId, snapshot));
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

    private static async Task<(string? pid, string? wrId)> ResolveWorkflowRunIdAsync(int number, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery)
    {
        var pid = await ResolveProjectIdAsync(projectId, grains);
        if (pid is null) return (null, null);

        var issue = await issuesQuery.GetInfoAsync(pid, number);
        return (pid, issue?.WorkflowRunId);
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

    private static object WorkflowVarsResponse(int issueNumber, string workflowRunId, WorkflowVariablesSnapshot? snapshot) => new
    {
        issueNumber,
        workflowRunId,
        affected = "future-dispatches",
        vars = SectionValue(snapshot?.Variables, "vars"),
        stageVars = StageVarsValues(snapshot?.StageVariables),
    };

    private static JsonElement? SectionValue(string? variables, string section)
    {
        if (string.IsNullOrWhiteSpace(variables)) return null;
        using var document = JsonDocument.Parse(variables);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(section, out var value)
            ? value.Clone()
            : null;
    }

    private static JsonElement? VarValue(string? variables, string name)
    {
        var vars = SectionValue(variables, "vars");
        return vars is { ValueKind: JsonValueKind.Object }
            && vars.Value.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;
    }

    private static JsonElement? StageSectionValue(Dictionary<string, Dictionary<string, string>>? stageVariables, string stage, string section) =>
        stageVariables is not null
        && stageVariables.TryGetValue(stage, out var sections)
        && sections.TryGetValue(section, out var value)
            ? JsonValue(value)
            : null;

    private static JsonElement? StageVarValue(Dictionary<string, Dictionary<string, string>>? stageVariables, string stage, string name)
    {
        var vars = StageSectionValue(stageVariables, stage, "vars");
        return vars is { ValueKind: JsonValueKind.Object }
            && vars.Value.TryGetProperty(name, out var value)
            ? value.Clone()
            : null;
    }

    private static JsonElement? JsonValue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, Dictionary<string, JsonElement?>>? StageVariableValues(Dictionary<string, Dictionary<string, string>>? stageVariables)
    {
        if (stageVariables is null || stageVariables.Count == 0) return null;

        return stageVariables.ToDictionary(
            stage => stage.Key,
            stage => stage.Value.ToDictionary(section => section.Key, section => JsonValue(section.Value), StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement?>? StageVarsValues(Dictionary<string, Dictionary<string, string>>? stageVariables)
    {
        if (stageVariables is null || stageVariables.Count == 0) return null;

        return stageVariables
            .Select(stage => (stage.Key, Vars: StageSectionValue(stageVariables, stage.Key, "vars")))
            .Where(stage => stage.Vars is not null)
            .ToDictionary(stage => stage.Key, stage => stage.Vars, StringComparer.OrdinalIgnoreCase);
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
    Dictionary<string, object?>? AgentConfig = null,
    Dictionary<string, string>? StageModels = null,
    string? WorkflowProfileId = null,
    string? ProjectId = null);

public record UpdateIssueRequest(
    string? Title = null,
    string? Body = null,
    string[]? Labels = null,
    string? Priority = null,
    string? Model = null,
    Dictionary<string, object?>? AgentConfig = null,
    Dictionary<string, string>? StageModels = null,
    Dictionary<string, Dictionary<string, string>>? StageVariables = null);

public record SetStageVariablesRequest(Dictionary<string, string> Variables);

public record RejectRequest(string? Reason);

public sealed record RebaseRequest(string? BaseBranch = null, RuntimeTaskRequest? ConflictResolver = null);

public sealed record RuntimeTaskRequest(
    string? Id = null,
    string? Title = null,
    string? Uses = null,
    Dictionary<string, object?>? With = null);
