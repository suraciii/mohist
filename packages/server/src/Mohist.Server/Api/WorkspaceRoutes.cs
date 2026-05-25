using Mohist.Server.Issue.Grains;
using Mohist.Server.Project.Grains;
using Mohist.Server.Workspace;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    private const string ProjectKey = "projects";

    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues/{number:int}");

        issues.MapGet("/worktree-status", async (int number, string? projectId, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var status = await git.GetWorktreeStatusAsync(project.Path, project.Name, number, project.BaseBranch);
            return ApiResults.Ok(status);
        });

        issues.MapGet("/diff", async (int number, string? projectId, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var branchExists = await git.BranchExistsAsync(project.Path, $"mo/issue-{number}");
            if (!branchExists)
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var head = $"mo/issue-{number}";
            var diff = await git.GetDiffAsync(project.Path, project.BaseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(project.Path, project.BaseBranch, head) ?? project.BaseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(project.Path, project.BaseBranch, head);
            var commits = await git.GetCommitsAsync(project.Path, project.BaseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = project.BaseBranch,
                head,
                mergeBase,
                ahead,
                behind,
                canFastForward = behind == 0,
                comparison = "merge-base",
                summary = new { filesChanged = diff.Files.Count, commits = commits.Length, additions = diff.TotalAdditions, deletions = diff.TotalDeletions },
                files = diff.Files,
            });
        });

        issues.MapGet("/commits", async (int number, string? projectId, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var head = $"mo/issue-{number}";
            if (!await git.BranchExistsAsync(project.Path, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var commits = await git.GetCommitsAsync(project.Path, project.BaseBranch, head);
            var diff = await git.GetDiffAsync(project.Path, project.BaseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(project.Path, project.BaseBranch, head) ?? project.BaseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(project.Path, project.BaseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = project.BaseBranch,
                head,
                mergeBase,
                ahead,
                behind,
                canFastForward = behind == 0,
                comparison = "merge-base",
                summary = new { filesChanged = diff.Files.Count, commits = commits.Length, additions = diff.TotalAdditions, deletions = diff.TotalDeletions },
                commits = commits.Select(c => new
                {
                    c.Hash,
                    c.ShortHash,
                    c.Message,
                    c.Author,
                    c.Date,
                    filesChanged = c.Files.Length,
                    additions = 0,
                    deletions = 0,
                    c.Files,
                }),
            });
        });

        issues.MapGet("/commits/{hash}/diff", async (int number, string hash, string? projectId, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var head = $"mo/issue-{number}";
            if (!await git.BranchExistsAsync(project.Path, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found", hash, diff = "" });

            var diff = await git.GetCommitDiffAsync(project.Path, hash);
            return diff is null
                ? ApiResults.NotFound($"Commit {hash} not found")
                : ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff });
        });

        issues.MapGet("/file-content", async (int number, string path, string? projectId, IGitService git, IGrainFactory grains) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var baseContent = await git.GetFileContentAsync(project.Path, project.BaseBranch, path);
            var headContent = await git.GetFileContentAsync(project.Path, $"mo/issue-{number}", path);

            return ApiResults.Ok(new { @base = baseContent, head = headContent });
        });

        issues.MapPost("/cleanup", async (int number, string? projectId, IGrainFactory grains, IGitService git, WorkflowProjectionService projection) =>
        {
            var pid = await ResolveProjectIdAsync(projectId, grains);
            if (pid is null) return ApiResults.BadRequest("No active project");

            var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
            var project = await projectsGrain.GetByIdAsync(pid);
            if (project is null) return ApiResults.NotFound("Project not found");

            var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
            try
            {
                await grain.GetInfoAsync();
                var workflow = await grain.GetWorkflowStatusAsync();
                if (IsWorkflowActive(workflow))
                {
                    return ApiResults.Conflict("Cannot remove a worktree while the issue workflow is active", "worktree_active");
                }

                var activeAgents = await projection.ListActiveAgentsAsync(pid);
                if (activeAgents.Any(a => a.IssueNumber == number))
                {
                    return ApiResults.Conflict("Cannot remove a worktree while an agent is running", "worktree_agent_running");
                }

                var removal = await git.RemoveWorktreeAsync(project.Path, project.Name, number);
                if (removal.Status == "failed")
                {
                    return ApiResults.Conflict(removal.Message, "worktree_cleanup_failed", removal);
                }

                return ApiResults.Ok(ToCleanupResponse(removal));
            }
            catch (InvalidOperationException)
            {
                return ApiResults.NotFound($"Issue #{number} not found");
            }
        });

        return app;
    }

    private static bool IsWorkflowActive(IssueWorkflowStatus? workflow)
    {
        var status = workflow?.Workflow?.Status;
        return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "AwaitingApproval", StringComparison.OrdinalIgnoreCase);
    }

    private static object ToCleanupResponse(WorktreeRemovalResult removal) => new
    {
        removed = removal.Removed,
        message = removal.Message,
        resources = new[]
        {
            new
            {
                type = "worktree",
                status = removal.Status,
                path = removal.Path,
                reason = removal.Reason,
            },
        },
    };

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, IGrainFactory grains)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
        var projectsGrain = grains.GetGrain<IProjectGrain>(ProjectKey);
        var projects = await projectsGrain.GetAllAsync();
        return projects.Count == 1 ? projects[0].Id : null;
    }
}
