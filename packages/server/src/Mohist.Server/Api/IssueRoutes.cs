using Mohist.Server.Grains;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Queries;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Queries;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Workflow.Views;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Workspace;
using System.Text.Json;

namespace Mohist.Server.Api;

public static class IssueRoutes
{
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
            IssueQueryService issuesQuery,
            ProjectQueryService projectsQuery) =>
        {
            if (all == true && string.IsNullOrWhiteSpace(projectId))
            {
                var projects = await projectsQuery.ListAllAsync();
                var allIssues = new List<IssueReadModel>();
                foreach (var listedProject in projects)
                    allIssues.AddRange(await issuesQuery.ListAsync(listedProject.Id, listedProject, stage, label, priority, archived, all));

                return ApiResults.Ok(allIssues.OrderBy(i => i.Number).ToList());
            }

            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var project = await projectsQuery.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            var list = await issuesQuery.ListAsync(pid, project, stage, label, priority, archived, all);

            return ApiResults.Ok(list);
        });

        issues.MapPost("/", async (CreateIssueRequest req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var pid = req.ProjectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var project = await projectsQuery.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var repository = project.GetRepository(req.RepositoryName);

            var counter = grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(pid));
            var number = await counter.NextAsync();
            var issueGrain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            await issueGrain.CreateAsync(pid, number, req.Title, req.Body, req.Labels, req.Priority, repository);
            var issue = await issuesQuery.GetAsync(pid, number);
            return Results.Json(new { success = true, data = issue }, statusCode: 201);
        });

        issues.MapGet("/{number:int}", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var project = await projectsQuery.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");
            var info = await issuesQuery.GetAsync(pid, number, project);
            return info is not null ? ApiResults.Ok(info) : ApiResults.NotFound($"Issue #{number} not found");
        });

        issues.MapPatch("/{number:int}", async (int number, string projectId, UpdateIssueRequest req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                await grain.UpdateFullAsync(new UpdateIssueData(
                    req.Title, req.Body, req.Labels, req.Priority));
                var info = await issuesQuery.GetAsync(pid, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/prerequisites", async (int number, string projectId, AddPrerequisiteRequest req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                var result = await grain.AddPrerequisiteAsync(req.PrerequisiteNumber);
                if (!result.Success)
                    return ApiResults.NotFound(result.Message);

                var info = await issuesQuery.GetAsync(pid, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapDelete("/{number:int}/prerequisites/{prerequisiteNumber:int}", async (int number, int prerequisiteNumber, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                await grain.RemovePrerequisiteAsync(prerequisiteNumber);
                var info = await issuesQuery.GetAsync(pid, number);
                return ApiResults.Ok(info);
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/start", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                var eligibility = await grain.GetStartEligibilityAsync();
                if (!eligibility.Startable)
                    return ApiResults.Conflict(eligibility.Message ?? "Issue is waiting for prerequisites", "start_blocked", eligibility);

                await grain.StartWorkAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
        });

        issues.MapPost("/{number:int}/comments", async (int number, string projectId, AddCommentRequest req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                var comment = await grain.AddCommentAsync(req.Body);
                return Results.Json(new { success = true, data = new { id = comment.Id, body = comment.Body } });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/close", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                await grain.CancelAsync();
                return ApiResults.Ok();
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapPost("/{number:int}/reopen", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
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

        issues.MapPost("/{number:int}/archive", async (int number, string projectId, IGrainFactory grains, IGitService git, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var repoPath = issue.Repository?.Path ?? ".";
            var projectName = issue.ProjectName ?? "project";

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                await grain.ArchiveAsync();
                var cleanup = await git.RemoveWorktreeAsync(repoPath, projectName, number);
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

        issues.MapPost("/{number:int}/unarchive", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
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

        issues.MapPost("/archive-completed", async (string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery, IGitService git) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var all = await issuesQuery.ListAsync(pid, null, all: true);
            var completed = all.Where(i => i.Stage == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Stage != "done" && i.ArchivedAt == null).ToList();
            var cleanupFailed = 0;

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, issue.Number));
                try
                {
                    var repoPath = issue.Repository?.Path ?? ".";
                    var projectName = issue.ProjectName ?? "project";
                    await grain.ArchiveAsync();
                    var cleanup = await git.RemoveWorktreeAsync(repoPath, projectName, issue.Number);
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

        issues.MapGet("/{number:int}/workflow/status", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
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

        issues.MapGet("/{number:int}/workflow/timeline", async (int number, string projectId, IGrainFactory grains, WorkflowProjectionService projection) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var timeline = await projection.GetTimelineAsync(pid, number);
            return timeline is not null ? ApiResults.Ok(timeline) : ApiResults.NotFound("Workflow not found");
        });

        issues.MapGet("/{number:int}/coder-sessions", async (int number, string projectId, IGrainFactory grains, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await sessions.ListSummariesByIssueAsync(pid, number));
        });

        issues.MapGet("/{number:int}/coder-sessions/{*sessionId}", async (int number, string sessionId, string projectId, IGrainFactory grains, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var detail = await sessions.GetTranscriptAsync(pid, number, sessionId);
            return detail is null ? ApiResults.NotFound($"Coder session {sessionId} not found") : ApiResults.Ok(detail);
        });

        issues.MapPost("/{number:int}/resume", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ResumeAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rebase", async (int number, string projectId, RebaseRequest? req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");

            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var workflow = grains.GetGrain<IWorkflowGrain>(wrId);
            if (await workflow.HasIncompleteTaskWithUsesAsync("mohist/rebase"))
                return ApiResults.Conflict("Rebase task is already pending", "rebase_already_pending");

            var baseBranch = string.IsNullOrWhiteSpace(req?.BaseBranch) ? issue.Repository?.BaseBranch ?? "main" : req!.BaseBranch!;
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

        issues.MapPost("/{number:int}/approve", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).ApproveAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/reject", async (int number, string projectId, RejectRequest? req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RejectAsync(req?.Reason);
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/retry", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RetryAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/rerun", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).RerunAsync();
            return ApiResults.Ok();
        });

        issues.MapPost("/{number:int}/force-stop", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        // Stage-level variable overrides
        issues.MapGet("/{number:int}/stage-variables", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var info = await issuesQuery.GetInfoAsync(pid, number);
            if (info is null) return ApiResults.NotFound($"Issue #{number} not found");
            return ApiResults.Ok(new { stageVariables = info.StageVariables });
        });

        issues.MapPut("/{number:int}/stage-variables/{stage}", async (int number, string stage, SetStageVariablesRequest req, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                var info = await issuesQuery.GetInfoAsync(pid, number);
                if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

                await grain.UpdateFullAsync(new UpdateIssueData());
                return ApiResults.Ok(new { stage, variables = req.Variables, note = "Stage variables are now managed via workflow profile" });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        issues.MapDelete("/{number:int}/stage-variables/{stage}", async (int number, string stage, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                var info = await issuesQuery.GetInfoAsync(pid, number);
                if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

                await grain.UpdateFullAsync(new UpdateIssueData());
                return ApiResults.Ok(new { stage, removed = true, note = "Stage variables are now managed via workflow profile" });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        // Issue-scoped active workflow definition vars. Mohist does not expose workflow runs as standalone product resources.
        issues.MapGet("/{number:int}/workflow/yaml", async (int number, string projectId, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var yaml = await reader.GetDefinitionYamlAsync(wrId);
            return yaml is null
                ? ApiResults.NotFound("Workflow definition not found")
                : ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, yaml });
        });

        issues.MapGet("/{number:int}/workflow/vars", async (int number, string projectId, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await reader.GetVariablesAsync(wrId);
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, vars = SectionValue(snapshot?.Variables, "vars") });
        });

        issues.MapGet("/{number:int}/workflow/vars/{name}", async (int number, string name, string projectId, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await reader.GetVariablesAsync(wrId);
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, name, value = VarValue(snapshot?.Variables, name) });
        });

        issues.MapPatch("/{number:int}/workflow/vars/{name}", async (int number, string name, JsonElement patch, string projectId, IGrainFactory grains, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            await grains.GetGrain<IWorkflowGrain>(wrId).PatchVariablesAsync("vars", JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [name] = patch }));
            var snapshot = await reader.GetVariablesAsync(wrId);
            return ApiResults.Ok(WorkflowVarsResponse(number, wrId, snapshot));
        });

        issues.MapGet("/{number:int}/workflow/stages/{stage}/vars/{name}", async (int number, string stage, string name, string projectId, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            var snapshot = await reader.GetVariablesAsync(wrId);
            return ApiResults.Ok(new { issueNumber = number, workflowRunId = wrId, stage, name, value = StageVarValue(snapshot?.StageVariables, stage, name) });
        });

        issues.MapPatch("/{number:int}/workflow/stages/{stage}/vars/{name}", async (int number, string stage, string name, JsonElement patch, string projectId, IGrainFactory grains, WorkflowQueryService reader, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.Conflict("Issue has no active workflow", "no_active_workflow");

            await grains.GetGrain<IWorkflowGrain>(wrId).PatchStageVariablesAsync(stage, "vars", JsonSerializer.Serialize(new Dictionary<string, JsonElement> { [name] = patch }));
            var snapshot = await reader.GetVariablesAsync(wrId);
            return ApiResults.Ok(WorkflowVarsResponse(number, wrId, snapshot));
        });

         return app;
    }

    private static string BuildRebaseTaskWith(string baseBranch, RuntimeTaskRequest? conflictResolver)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
        };

        if (conflictResolver?.With is not null || conflictResolver?.Uses is not null)
        {
            with["conflictResolver"] = new Dictionary<string, object?>
            {
                ["title"] = string.IsNullOrWhiteSpace(conflictResolver.Title) ? "Resolve rebase conflicts" : conflictResolver.Title,
                ["with"] = conflictResolver.With,
            };
        }
        else
        {
            with["conflictResolver"] = new Dictionary<string, object?>
            {
                ["title"] = "Resolve rebase conflicts",
                ["with"] = DefaultConflictResolverWith(),
            };
        }

        return JsonSerializer.Serialize(with, WorkflowVariableJson.Options);
    }

    private static object WorkflowVarsResponse(int issueNumber, string workflowRunId, WorkflowVariablesView? snapshot) => new
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

    private static Dictionary<string, object?> DefaultConflictResolverWith() => new()
    {
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
    string? RepositoryName = null,
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

public record AddPrerequisiteRequest(int PrerequisiteNumber);

public record AddCommentRequest(string Body);
