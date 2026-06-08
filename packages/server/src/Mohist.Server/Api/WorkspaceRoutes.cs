using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        issues.MapGet("/diff", async (
            HttpContext context, int number,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var (repoPath, _) = ResolveRepo(issue);
            if (!await BranchExistsAsync(git, issue, repoPath))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            if (string.IsNullOrEmpty(issue.WorkflowRunId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow" });

            try
            {
                var result = await git.GetDiffAsync(repoPath, ResolveRepo(issue).BaseBranch, $"mo/issue-{number}");
                var (ahead, behind) = await git.GetAheadBehindAsync(repoPath, ResolveRepo(issue).BaseBranch, $"mo/issue-{number}");
                var commits = await git.GetCommitsAsync(repoPath, ResolveRepo(issue).BaseBranch, $"mo/issue-{number}");
                var mergeBase = await git.GetMergeBaseAsync(repoPath, ResolveRepo(issue).BaseBranch, $"mo/issue-{number}");

                return ApiResults.Ok(new
                {
                    available = true,
                    reason = (string?)null,
                    @base = ResolveRepo(issue).BaseBranch,
                    head = $"mo/issue-{number}",
                    mergeBase,
                    ahead,
                    behind,
                    canFastForward = behind == 0,
                    comparison = "merge-base",
                    summary = new { filesChanged = result.Files.Count, commits = commits.Length, additions = result.TotalAdditions, deletions = result.TotalDeletions },
                    files = result.Files,
                });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out" });
            }
        });

        issues.MapGet("/commits", async (
            HttpContext context, int number,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var (repoPath, _) = ResolveRepo(issue);
            if (!await BranchExistsAsync(git, issue, repoPath))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found" });

            if (string.IsNullOrEmpty(issue.WorkflowRunId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow" });

            try
            {
                var (_, baseBranch) = ResolveRepo(issue);
                var head = $"mo/issue-{number}";
                var commits = await git.GetCommitsAsync(repoPath, baseBranch, head);
                var result = await git.GetDiffAsync(repoPath, baseBranch, head);
                var (ahead, behind) = await git.GetAheadBehindAsync(repoPath, baseBranch, head);
                var mergeBase = await git.GetMergeBaseAsync(repoPath, baseBranch, head);

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
                    summary = new { filesChanged = result.Files.Count, commits = commits.Length, additions = result.TotalAdditions, deletions = result.TotalDeletions },
                    commits,
                });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out" });
            }
        });

        issues.MapGet("/commits/{hash}/diff", async (
            HttpContext context, int number, string hash,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var (repoPath, _) = ResolveRepo(issue);
            if (!await BranchExistsAsync(git, issue, repoPath))
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Branch not found", hash, diff = "" });

            if (string.IsNullOrEmpty(issue.WorkflowRunId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow", hash, diff = "" });

            try
            {
                var diff = await git.GetCommitDiffAsync(repoPath, hash);

                if (diff is null)
                    return ApiResults.NotFound($"Commit {hash} not found");

                return ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out", hash, diff = "" });
            }
        });

        issues.MapGet("/worktree-status", async (
            HttpContext context, int number,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrEmpty(issue.WorkflowRunId))
                return ApiResults.Ok(new { exists = false });

            try
            {
                var projectName = issue.ProjectName ?? issue.ProjectId;
                var (repoPath, baseBranch) = ResolveRepo(issue);
                var result = await git.GetWorktreeStatusAsync(repoPath, projectName, number, baseBranch);
                return ApiResults.Ok(result);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { exists = false, reason = "timeout" });
            }
        });

        issues.MapGet("/file-content", async (
            HttpContext context, int number, string path,
            IGitService git,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrEmpty(issue.WorkflowRunId))
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null });

            try
            {
                var (_, baseBranch) = ResolveRepo(issue);
                var baseContent = await git.GetFileContentAsync(ResolveRepo(issue).RepoPath, baseBranch, path);
                var headContent = await git.GetFileContentAsync(ResolveRepo(issue).RepoPath, $"mo/issue-{number}", path);
                return ApiResults.Ok(new { @base = baseContent, head = headContent });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null });
            }
        });

        issues.MapPost("/cleanup", async (HttpContext context, int number, IGrainFactory grains, IGitService git, WorkflowActivityQuerier projection, IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
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
            var projectName = issue.ProjectName ?? "project";
            var removal = await git.RemoveWorktreeAsync(repoPath, projectName, number);
            if (removal.Status == "failed")
            {
                return ApiResults.Conflict(removal.Message, "worktree_cleanup_failed", removal);
            }

            return ApiResults.Ok(ToCleanupResponse(removal));
        });

        return app;
    }

    private static IResult? CheckRepositoryConfig(IssueReadModel? issue) =>
        IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue);

    private static (string RepoPath, string BaseBranch) ResolveRepo(IssueReadModel issue)
    {
        var repo = issue.Repository
            ?? throw new InvalidOperationException(
                "Issue repository context is not resolved; check IssueRepositoryResolutionHelpers.CheckRepositoryConfigured first.");
        var repoPath = string.IsNullOrWhiteSpace(repo.Path) ? IssueRepositoryResolutionHelpers.DefaultRepoPath : repo.Path;
        var baseBranch = string.IsNullOrWhiteSpace(repo.BaseBranch) ? IssueRepositoryResolutionHelpers.DefaultBaseBranch : repo.BaseBranch;
        return (repoPath, baseBranch);
    }

    private static Task<bool> BranchExistsAsync(IGitService git, IssueReadModel issue, string repoPath)
        => git.BranchExistsAsync(repoPath, $"mo/issue-{issue.Number}");

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
}
