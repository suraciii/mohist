using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Queries;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Workflow.Projection;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues/{number:int}");

        issues.MapGet("/worktree-status", async (int number, string? projectId, IGitService git, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var (repoPath, baseBranch) = ResolveRepo(issue);
            var status = await git.GetWorktreeStatusAsync(repoPath, issue.ProjectName ?? "project", number, baseBranch);
            return ApiResults.Ok(status);
        });

        issues.MapGet("/diff", async (int number, string? projectId, IGitService git, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var (repoPath, baseBranch) = ResolveRepo(issue);
            var head = $"mo/issue-{number}";

            var branchExists = await git.BranchExistsAsync(repoPath, head);
            if (!branchExists)
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var diff = await git.GetDiffAsync(repoPath, baseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(repoPath, baseBranch, head) ?? baseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(repoPath, baseBranch, head);
            var commits = await git.GetCommitsAsync(repoPath, baseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = baseBranch,
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

        issues.MapGet("/commits", async (int number, string? projectId, IGitService git, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var (repoPath, baseBranch) = ResolveRepo(issue);
            var head = $"mo/issue-{number}";

            if (!await git.BranchExistsAsync(repoPath, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            var commits = await git.GetCommitsAsync(repoPath, baseBranch, head);
            var diff = await git.GetDiffAsync(repoPath, baseBranch, head);
            var mergeBase = await git.GetMergeBaseAsync(repoPath, baseBranch, head) ?? baseBranch;
            var (ahead, behind) = await git.GetAheadBehindAsync(repoPath, baseBranch, head);
            return ApiResults.Ok(new
            {
                available = true,
                reason = (string?)null,
                @base = baseBranch,
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

        issues.MapGet("/commits/{hash}/diff", async (int number, string hash, string? projectId, IGitService git, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var (repoPath, baseBranch) = ResolveRepo(issue);
            var head = $"mo/issue-{number}";

            if (!await git.BranchExistsAsync(repoPath, head))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found", hash, diff = "" });

            var diff = await git.GetCommitDiffAsync(repoPath, hash);
            return diff is null
                ? ApiResults.NotFound($"Commit {hash} not found")
                : ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff });
        });

        issues.MapGet("/file-content", async (int number, string path, string? projectId, IGitService git, IGrainFactory grains, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var (repoPath, baseBranch) = ResolveRepo(issue);
            var baseContent = await git.GetFileContentAsync(repoPath, baseBranch, path);
            var headContent = await git.GetFileContentAsync(repoPath, $"mo/issue-{number}", path);

            return ApiResults.Ok(new { @base = baseContent, head = headContent });
        });

        issues.MapPost("/cleanup", async (int number, string? projectId, IGrainFactory grains, IGitService git, WorkflowProjectionService projection, IssueQueryService issuesQuery, ProjectQueryService projectsQuery) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            try
            {
                var grain = grains.GetGrain<IIssueGrain>($"{pid}:{number}");
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

                var (repoPath, _) = ResolveRepo(issue);
                var project = await projectsQuery.GetByIdAsync(pid);
                var projectName = project?.Name ?? issue.ProjectName ?? "project";
                var removal = await git.RemoveWorktreeAsync(repoPath, projectName, number);
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

    private static (string RepoPath, string BaseBranch) ResolveRepo(IssueReadModel issue)
    {
        var repo = issue.Repository;
        var repoPath = repo?.Path ?? ".";
        var baseBranch = repo?.BaseBranch ?? "main";
        return (repoPath, baseBranch);
    }

    private static async Task<(string? ProjectId, IssueReadModel? Issue)> ResolveIssueAsync(
        int number, string? projectId, ProjectQueryService projectsQuery, IssueQueryService issuesQuery)
    {
        var pid = await ResolveProjectIdAsync(projectId, projectsQuery);
        if (pid is null) return (null, null);

        var issue = await issuesQuery.GetAsync(pid, number);
        return (pid, issue);
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

    private static async Task<string?> ResolveProjectIdAsync(string? projectId, ProjectQueryService projectsQuery)
    {
        if (!string.IsNullOrWhiteSpace(projectId)) return projectId;
        var resolved = await projectsQuery.ResolveSingleAsync();
        return resolved?.Id;
    }
}
