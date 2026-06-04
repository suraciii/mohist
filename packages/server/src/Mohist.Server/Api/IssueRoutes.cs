using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Domain;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Issue.Storage;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Queries;
using Mohist.Server.Sessions.Queries;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Errors;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Infrastructure;
using Mohist.Server.Workflow.Projection;
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
        var projectIssues = app.MapGroup("/api/projects/{projectId}/issues");

        projectIssues.MapGet("/{number:int}/workflow-profile", async (string projectId, int number, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var response = await BuildIssueWorkflowProfileResponseAsync(projectId, number, issueProfileManager, issuesQuery, projectsQuery);
            return response is null ? ApiResults.NotFound($"Issue #{number} not found") : ApiResults.Ok(response);
        });

        projectIssues.MapPut("/{number:int}/workflow-profile/template", async (string projectId, int number, IssueTemplateRequest req, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var yaml = req.Yaml ?? req.Template;
            if (!string.IsNullOrWhiteSpace(req.ProjectTemplateId) && !string.IsNullOrWhiteSpace(yaml))
                return ApiResults.BadRequest("Specify either projectTemplateId or yaml, not both");
            if (string.IsNullOrWhiteSpace(req.ProjectTemplateId) && string.IsNullOrWhiteSpace(yaml))
                return ApiResults.BadRequest("Specify either projectTemplateId or yaml");

            var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            try
            {
                await issueProfileManager.UpdateTemplateAsync($"{projectId}:{number}", new IssueTemplateUpdateRequest(
                    ProjectTemplateId: req.ProjectTemplateId,
                    Template: yaml));
            }
            catch (YamlException ex)
            {
                return ApiResults.Fail("YAML syntax error: " + ex.Message, 400, "yaml_syntax");
            }
            catch (InvalidOperationException ex)
            {
                return ApiResults.Fail("Workflow definition error: " + ex.Message, 400, "workflow_shape");
            }

            var response = await BuildIssueWorkflowProfileResponseAsync(projectId, number, issueProfileManager, issuesQuery, projectsQuery);
            return ApiResults.Ok(response!);
        });

        projectIssues.MapDelete("/{number:int}/workflow-profile/template", async (string projectId, int number, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            await issueProfileManager.UpdateTemplateAsync($"{projectId}:{number}", new IssueTemplateUpdateRequest());
            var response = await BuildIssueWorkflowProfileResponseAsync(projectId, number, issueProfileManager, issuesQuery, projectsQuery);
            return ApiResults.Ok(response!);
        });

        projectIssues.MapGet("/{number:int}/workflow-profile/variables", async (string projectId, int number, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.GetVariablesAsync($"{projectId}:{number}"));
        });

        projectIssues.MapPut("/{number:int}/workflow-profile/variables", async (string projectId, int number, VariableBundle bundle, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.SetVariablesAsync($"{projectId}:{number}", bundle));
        });

        projectIssues.MapPatch("/{number:int}/workflow-profile/variables", async (string projectId, int number, VariableBundle patch, IssueWorkflowProfileManager issueProfileManager, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var issue = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
            if (issue is null) return ApiResults.NotFound($"Issue #{number} not found");

            return ApiResults.Ok(await issueProfileManager.PatchVariablesAsync($"{projectId}:{number}", patch));
        });

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
            catch (MissingPromptsException ex)
            {
                return ApiResults.Fail(ex.Message, 400, "missing_prompts", new { missingKeys = ex.MissingKeys });
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

        issues.MapGet("/{number:int}/coder-sessions", async (int number, string projectId, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            return ApiResults.Ok(await sessions.ListSummariesByIssueAsync(pid, number));
        });

        issues.MapGet("/{number:int}/sessions/{name}", async (int number, string name, string projectId, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var metadata = await sessions.GetSessionMetadataAsync(pid, number, name);
            return metadata is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(metadata);
        });

        issues.MapGet("/{number:int}/sessions/{name}/events", async (int number, string name, string projectId, WorkflowAgentSessionQueryService sessions) =>
        {
            var pid = projectId;
            if (pid is null) return ApiResults.BadRequest("No active project");
            var events = await sessions.GetSessionEventsAsync(pid, number, name);
            return events is null ? ApiResults.NotFound($"Session {name} not found") : ApiResults.Ok(events);
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

    private static async Task<IssueWorkflowProfileResponse?> BuildIssueWorkflowProfileResponseAsync(
        string projectId,
        int number,
        IssueWorkflowProfileManager issueProfileManager,
        IssueQueryService issuesQuery,
        ProjectQueryService projectsQuery)
    {
        var info = await issuesQuery.GetInfoAsync(projectId, number, await projectsQuery.GetByIdAsync(projectId));
        if (info is null) return null;

        var issueKey = $"{projectId}:{number}";
        var state = await issueProfileManager.GetStateAsync(issueKey);
        var variables = state.Variables;
        var template = state.Template;
        var yaml = template is null ? null : WorkflowYamlSerializer.ToYaml(template);
        var profileId = template?.Id ?? state.SourceTemplateId ?? "mohist/default";
        var updateMode = template is not null ? "Custom" : "Reference";

        return new IssueWorkflowProfileResponse(
            IssueNumber: number,
            ProjectId: projectId,
            IssueKey: issueKey,
            SourceTemplateId: state.SourceTemplateId,
            HasCustomTemplate: state.HasCustomTemplate,
            Yaml: yaml,
            WorkflowRunId: info.WorkflowRunId,
            ProfileId: profileId,
            UpdateMode: updateMode,
            Variables: variables,
            UpdatedAt: state.UpdatedAt?.ToString("O") ?? info.UpdatedAt);
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

public record RejectRequest(string? Reason);

public sealed record RebaseRequest(string? BaseBranch = null, RuntimeTaskRequest? ConflictResolver = null);

public sealed record RuntimeTaskRequest(
    string? Id = null,
    string? Title = null,
    string? Uses = null,
    Dictionary<string, object?>? With = null);

public record AddPrerequisiteRequest(int PrerequisiteNumber);

public record AddCommentRequest(string Body);

public record IssueTemplateRequest(string? ProjectTemplateId = null, string? Yaml = null, string? Template = null);

public sealed record IssueWorkflowProfileResponse(
    int IssueNumber,
    string ProjectId,
    string IssueKey,
    string? SourceTemplateId,
    bool HasCustomTemplate,
    string? Yaml,
    string? WorkflowRunId,
    string ProfileId,
    string UpdateMode,
    VariableBundle Variables,
    string UpdatedAt);
