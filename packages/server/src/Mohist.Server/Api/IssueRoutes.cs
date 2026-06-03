using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Queries;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Queries;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Errors;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Workflow.Views;
using Mohist.Server.Workflow.Queries;
using Mohist.Server.Infrastructure.Workspace;
using System.Text.Json;
using YamlDotNet.Core;

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

        issues.MapPost("/", async (CreateIssueRequest req, string? projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery, IssueRepositoryResolver repositoryResolver) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return ApiResults.BadRequest("title is required");

            var pid = projectId ?? req.ProjectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var project = await projectsQuery.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var resolution = repositoryResolver.Resolve(project, req.RepositoryName);
            if (resolution.HasProblem)
                return ApiResults.BadRequest(resolution.Problem!.Message, IssueRepositoryResolutionHelpers.RepositoryProblemCodeToApiCode(resolution.Problem.Code));

            var counter = grains.GetGrain<IIssueCounterGrain>(GrainKey.IssueCounter(pid));
            var number = await counter.NextAsync();
            var issueGrain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            await issueGrain.CreateAsync(pid, number, req.Title, req.Body, req.Labels, req.Priority, resolution.Repository!.Name);
            var issue = await issuesQuery.GetAsync(pid, number, project);
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

        issues.MapPut("/{number:int}/workflow-profile", async (int number, string projectId, UpdateWorkflowProfileApiRequest req, IGrainFactory grains) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number));
            try
            {
                await grain.UpdateWorkflowProfileAsync(new WorkflowProfileUpdateRequest(req.ProfileId, req.DefinitionYaml));
                return ApiResults.Ok(new { updated = true });
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
            catch (ArgumentException ex)
            {
                return ApiResults.BadRequest(ex.Message);
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
            if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is { } repoError) return repoError;

            var repoPath = issue.Repository!.Path ?? IssueRepositoryResolutionHelpers.DefaultRepoPath;
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
            var completed = all.Where(i => i.Status == "done" && i.ArchivedAt == null).ToList();
            var skipped = all.Where(i => i.Status != "done" && i.ArchivedAt == null).ToList();
            var cleanupFailed = 0;

            foreach (var issue in completed)
            {
                var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, issue.Number));
                try
                {
                    if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is not null)
                    {
                        cleanupFailed++;
                        continue;
                    }
                    var repoPath = issue.Repository!.Path ?? IssueRepositoryResolutionHelpers.DefaultRepoPath;
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

        issues.MapGet("/{number:int}/workflow/sessions/{sessionName}", async (int number, string sessionName, string projectId, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var detail = await sessions.GetCurrentWorkflowTranscriptAsync(pid, number, sessionName);
            return detail is null ? ApiResults.NotFound($"Workflow session {sessionName} not found") : ApiResults.Ok(detail);
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
            if (IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue) is { } repoError) return repoError;

            var workflow = grains.GetGrain<IWorkflowGrain>(wrId);
            if (await workflow.HasIncompleteTaskWithUsesAsync("mohist/rebase"))
                return ApiResults.Conflict("Rebase task is already pending", "rebase_already_pending");

            var baseBranch = !string.IsNullOrWhiteSpace(req?.BaseBranch)
                ? req!.BaseBranch!
                : (string.IsNullOrWhiteSpace(issue.Repository!.BaseBranch) ? IssueRepositoryResolutionHelpers.DefaultBaseBranch : issue.Repository.BaseBranch);
            var taskId = $"rebase-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var task = new RuntimeTaskInput(
                taskId,
                $"Rebase onto {baseBranch}",
                "mohist/rebase",
                BuildRebaseTaskWith(baseBranch, issue.Repository!, req?.ConflictResolver),
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

        // Force-stop is implemented as workflow pause. The user can resume afterwards.
        // For terminal disposal, use /close (issue close -> workflow Stopped) or /stop.
        issues.MapPost("/{number:int}/force-stop", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            await grains.GetGrain<IWorkflowGrain>(wrId).PauseAsync("user-force-stop");
            return ApiResults.Ok();
        });

        // Stop is a terminal pause: the workflow run is permanently stopped (cannot be resumed).
        // The issue itself is NOT closed; the user can re-open or close it separately.
        issues.MapPost("/{number:int}/stop", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var wrId = (await issuesQuery.GetInfoAsync(projectId, number))?.WorkflowRunId; var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (wrId is null) return ApiResults.NotFound("No workflow run");
            try
            {
                await grains.GetGrain<IWorkflowGrain>(wrId).StopAsync("user-stop");
                return ApiResults.Ok();
            }
            catch (WorkflowDomainException ex)
            {
                return ApiResults.Conflict(ex.Message);
            }
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

        // =======================================================================
        // Issue Template
        // =======================================================================

        issues.MapPut("/{number:int}/template", async (int number, string projectId, IssueTemplateRequest req, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issueKey = $"{pid}:{number}";

            if (string.IsNullOrWhiteSpace(req.ProjectTemplateId) && string.IsNullOrWhiteSpace(req.Template))
                return ApiResults.BadRequest("Specify either projectTemplateId or template YAML");

            try
            {
                var row = await issueProfileManager.UpdateTemplateAsync(issueKey, new IssueTemplateUpdateRequest(
                    ProjectTemplateId: req.ProjectTemplateId,
                    Template: req.Template));
                return ApiResults.Ok(new
                {
                    issueKey,
                    sourceTemplateId = row.SourceTemplateId,
                    hasCustomTemplate = !string.IsNullOrWhiteSpace(row.TemplateJson)
                });
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.BadRequest(ex.Message);
            }
        });

        // =======================================================================
        // Issue Variables (GET / PUT / PATCH)
        // =======================================================================

        issues.MapGet("/{number:int}/variables", async (int number, string projectId, IssueWorkflowProfileManager issueProfileManager) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issueKey = $"{pid}:{number}";
            var variables = await issueProfileManager.GetVariablesAsync(issueKey);
            return ApiResults.Ok(variables);
        });

        issues.MapPut("/{number:int}/variables", async (int number, string projectId, VariableBundle bundle, IssueWorkflowProfileManager issueProfileManager) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issueKey = $"{pid}:{number}";
            var result = await issueProfileManager.SetVariablesAsync(issueKey, bundle);
            return ApiResults.Ok(result);
        });

        issues.MapPatch("/{number:int}/variables", async (int number, string projectId, VariableBundle patch, IssueWorkflowProfileManager issueProfileManager) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var issueKey = $"{pid}:{number}";
            var result = await issueProfileManager.PatchVariablesAsync(issueKey, patch);
            return ApiResults.Ok(result);
        });
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

         issues.MapGet("/{number:int}/workflow/profile/yaml", async (int number, string projectId, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var info = await issuesQuery.GetInfoAsync(pid, number, await projectsQuery.GetByIdAsync(pid));
            if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

            var key = $"{pid}:{number}";
            var profileRow = await issuesQuery.LoadIssueProfileAsync(key);
            var profile = profileRow is null ? null : IssueWorkflowProfileSnapshot.Deserialize(profileRow.StateJson);

            string? yaml = null;
            if (profile is not null)
            {
                yaml = WorkflowYamlSerializer.ToYaml(profile.Definition);
            }

            return ApiResults.Ok(new IssueWorkflowProfileYamlResponse(
                number,
                pid,
                yaml,
                info.WorkflowRunId,
                profile?.SourceProfileId ?? IssueWorkflowProfiles.DefaultId,
                profile?.UpdateMode.ToString() ?? "reference",
                info.UpdatedAt));
        });

        issues.MapPut("/{number:int}/workflow/profile/yaml", async (int number, string projectId, UpdateIssueWorkflowProfileYamlRequest req, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");

            var info = await issuesQuery.GetInfoAsync(pid, number, await projectsQuery.GetByIdAsync(pid));
            if (info is null) return ApiResults.NotFound($"Issue #{number} not found");

            Workflow.Domain.Definition.WorkflowDefinition definition;
            try
            {
                definition = WorkflowYamlSerializer.FromYaml(req.Yaml);
            }
            catch (YamlException ex)
            {
                return ApiResults.Fail("YAML syntax error: " + ex.Message, 400, "yaml_syntax");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Fail("Workflow definition error: " + ex.Message, 400, "workflow_shape");
            }

            var normalizedYaml = WorkflowYamlSerializer.ToYaml(definition);
            var key = $"{pid}:{number}";

            await grains.GetGrain<IIssueGrain>(GrainKey.Issue(pid, number))
                .UpdateWorkflowProfileAsync(new WorkflowProfileUpdateRequest(DefinitionYaml: normalizedYaml));

            var updatedInfo = await issuesQuery.GetInfoAsync(pid, number, await projectsQuery.GetByIdAsync(pid));
            if (updatedInfo is null) return ApiResults.NotFound($"Issue #{number} not found");

            var updatedProfileRow = await issuesQuery.LoadIssueProfileAsync(key);
            var updatedProfile = updatedProfileRow is null ? null : IssueWorkflowProfileSnapshot.Deserialize(updatedProfileRow.StateJson);

            return ApiResults.Ok(new IssueWorkflowProfileYamlResponse(
                number,
                pid,
                normalizedYaml,
                updatedInfo.WorkflowRunId,
                updatedProfile?.SourceProfileId ?? IssueWorkflowProfiles.DefaultId,
                updatedProfile?.UpdateMode.ToString() ?? "reference",
                updatedInfo.UpdatedAt));
        });

         return app;
    }

    private static string BuildRebaseTaskWith(string baseBranch, RepositoryInfo repository, RuntimeTaskRequest? conflictResolver)
    {
        var with = new Dictionary<string, object?>
        {
            ["baseBranch"] = baseBranch,
            ["repository"] = new Dictionary<string, object?>
            {
                ["name"] = repository.Name,
                ["path"] = repository.Path,
                ["remote"] = repository.Remote,
                ["baseBranch"] = repository.BaseBranch,
            },
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

public record UpdateWorkflowProfileApiRequest(
    string? ProfileId = null,
    string? DefinitionYaml = null);

public record UpdateIssueWorkflowProfileYamlRequest(string Yaml);
public record IssueTemplateRequest(string? ProjectTemplateId = null, string? Template = null);

public sealed record IssueWorkflowProfileYamlResponse(
    int IssueNumber,
    string ProjectId,
    string? Yaml,
    string? WorkflowRunId,
    string ProfileId,
    string UpdateMode,
    string UpdatedAt);

public record ValidationError(string Code, string Message)
{
    public ApiResponse<object> ToApiResponse() => new(false, Error: Message, Code: Code);
}

public static class IssueWorkflowProfiles
{
    public const string DefaultId = "mohist/default";
}
