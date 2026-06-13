using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Workflow.Domain.Run;

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
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Ok(Unavailable("not_started", "Issue has no active workflow"));

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path) || !Directory.Exists(workspace.Path))
                return ApiResults.Ok(Unavailable("workspace_removed", "The workflow workspace is not available"));

            var baseBranch = ResolveBaseBranch(issue);
            var head = WorkspaceHeadOrNull(workspace, issue.WorkflowRunId);
            if (head is null)
                return ApiResults.Ok(Unavailable("branch_missing", "Workspace head branch is not recorded for this run"));

            try
            {
                if (!await git.BranchExistsAsync(workspace.Path, head))
                    return ApiResults.Ok(Unavailable("branch_missing", "Workspace branch not found in workspace"));

                var result = await git.GetDiffAsync(workspace.Path, baseBranch, head);
                var (ahead, behind) = await git.GetAheadBehindAsync(workspace.Path, baseBranch, head);
                var commits = await git.GetCommitsAsync(workspace.Path, baseBranch, head);
                var mergeBase = await git.GetMergeBaseAsync(workspace.Path, baseBranch, head);
                var patches = result.Files.Select(f => new { path = f.File, diff = f.Diff }).ToArray();

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
                    files = result.Files,
                    patches,
                });
            }
            catch (Exception ex)
            {
                return ApiResults.Ok(Unavailable("git_error", ex.Message));
            }
        });

        issues.MapGet("/commits", async (
            HttpContext context, int number,
            IGitService git,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Ok(Unavailable("not_started", "Issue has no active workflow"));

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path) || !Directory.Exists(workspace.Path))
                return ApiResults.Ok(Unavailable("workspace_removed", "The workflow workspace is not available"));

            var baseBranch = ResolveBaseBranch(issue);
            var head = WorkspaceHeadOrNull(workspace, issue.WorkflowRunId);
            if (head is null)
                return ApiResults.Ok(Unavailable("branch_missing", "Workspace head branch is not recorded for this run"));

            try
            {
                if (!await git.BranchExistsAsync(workspace.Path, head))
                    return ApiResults.Ok(Unavailable("branch_missing", "Workspace branch not found in workspace"));

                var commits = await git.GetCommitsAsync(workspace.Path, baseBranch, head);
                var result = await git.GetDiffAsync(workspace.Path, baseBranch, head);
                var (ahead, behind) = await git.GetAheadBehindAsync(workspace.Path, baseBranch, head);
                var mergeBase = await git.GetMergeBaseAsync(workspace.Path, baseBranch, head);

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
            catch (Exception ex)
            {
                return ApiResults.Ok(Unavailable("git_error", ex.Message));
            }
        });

        issues.MapGet("/commits/{hash}/diff", async (
            HttpContext context, int number, string hash,
            IGitService git,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow", hash, diff = "" });

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path) || !Directory.Exists(workspace.Path))
                return ApiResults.Ok(new { available = false, reason = "workspace_removed", message = "The workflow workspace is not available", hash, diff = "" });

            var head = WorkspaceHeadOrNull(workspace, issue.WorkflowRunId);
            if (head is null)
                return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Workspace head branch is not recorded for this run", hash, diff = "" });

            try
            {
                if (!await git.BranchExistsAsync(workspace.Path, head))
                    return ApiResults.Ok(new { available = false, reason = "branch_missing", message = "Workspace branch not found in workspace", hash, diff = "" });

                var diff = await git.GetCommitDiffAsync(workspace.Path, hash);

                if (diff is null)
                    return ApiResults.NotFound($"Commit {hash} not found");

                return ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff });
            }
            catch (Exception ex)
            {
                return ApiResults.Ok(new { available = false, reason = "git_error", message = ex.Message, hash, diff = "" });
            }
        });

        issues.MapGet("/workspace-status", async (
            HttpContext context, int number,
            IGitService git,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = "not_started" });

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path))
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = "workspace_removed" });

            if (!Directory.Exists(workspace.Path))
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = "workspace_removed" });

            var head = WorkspaceHeadOrNull(workspace, issue.WorkflowRunId);
            if (head is null)
                return ApiResults.Ok(new WorkspaceStatus { Exists = true, Reason = "branch_missing" });

            try
            {
                var baseBranch = ResolveBaseBranch(issue);
                var result = await git.GetWorkspaceStatusAsync(workspace.Path, baseBranch, head);
                return ApiResults.Ok(result);
            }
            catch (Exception)
            {
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = "git_error" });
            }
        });

        issues.MapGet("/file-content", async (
            HttpContext context, int number, string path,
            IGitService git,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = "not_started" });

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path) || !Directory.Exists(workspace.Path))
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = "workspace_removed" });

            var head = WorkspaceHeadOrNull(workspace, issue.WorkflowRunId);
            if (head is null)
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = "branch_missing" });

            try
            {
                var baseBranch = ResolveBaseBranch(issue);
                var baseContent = await git.GetFileContentAsync(workspace.Path, baseBranch, path);
                var headContent = await git.GetFileContentAsync(workspace.Path, head, path);
                return ApiResults.Ok(new { @base = baseContent, head = headContent });
            }
            catch (Exception)
            {
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = "git_error" });
            }
        });

        issues.MapPost("/cleanup", async (HttpContext context, int number, IGrainFactory grains, IGitService git, WorkflowQuerier querier, WorkflowActivityQuerier projection, IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(issue.Id));
            var workflow = await grain.GetWorkflowStatusAsync();
            if (IsWorkflowActive(workflow))
            {
                return ApiResults.Conflict("Cannot clean workflow workspace while the issue workflow is active", "workspace_active");
            }

            var activeAgents = await projection.ListActiveAgentsAsync(pid);
            if (activeAgents.Any(a => a.IssueNumber == number))
            {
                return ApiResults.Conflict("Cannot clean workflow workspace while an agent is running", "workspace_agent_running");
            }

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path))
            {
                return ApiResults.Conflict("No workflow workspace to clean", "workspace_missing");
            }

            if (!Directory.Exists(workspace.Path))
            {
                return ApiResults.Ok(new
                {
                    removed = false,
                    message = "Workspace already removed",
                    resources = new[]
                    {
                        new
                        {
                            type = "workspace",
                            status = "missing",
                            path = workspace.Path,
                            reason = "workspace_missing",
                        },
                    },
                });
            }

            var removal = await git.RemoveWorkspaceAsync(workspace.Path);
            if (removal.Status == "failed")
            {
                return ApiResults.Conflict(removal.Message, "workspace_cleanup_failed", removal);
            }

            return ApiResults.Ok(ToCleanupResponse(removal));
        });

        return app;
    }

    private static IResult? CheckRepositoryConfig(IssueReadModel? issue) =>
        IssueRepositoryResolutionHelpers.CheckRepositoryConfigured(issue);

    private static string ResolveBaseBranch(IssueReadModel issue) =>
        ResolveRepoBaseBranch(issue);

    private static string ResolveRepoBaseBranch(IssueReadModel issue)
    {
        var repo = issue.Repository
            ?? throw new InvalidOperationException(
                "Issue repository context is not resolved; check IssueRepositoryResolutionHelpers.CheckRepositoryConfigured first.");
        return string.IsNullOrWhiteSpace(repo.BaseBranch) ? IssueRepositoryResolutionHelpers.DefaultBaseBranch : repo.BaseBranch;
    }

    private static async Task<WorkspaceIdentity?> ResolveWorkspaceAsync(WorkflowQuerier querier, IssueReadModel issue)
    {
        if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
            return null;
        return await querier.GetWorkspaceAsync(issue.WorkflowRunId);
    }

    /// <summary>
    /// Returns the workspace's recorded head ref. The runner materializes the
    /// per-run branch <c>mohist/run-${workflowRunId}</c> inside the workspace;
    /// review APIs use that ref instead of legacy <c>mo/issue-{N}</c>.
    /// </summary>
    private static string? WorkspaceHeadOrNull(WorkspaceIdentity? workspace, string? workflowRunId)
    {
        if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.Branch))
            return workspace.Branch;
        if (string.IsNullOrWhiteSpace(workflowRunId))
            return null;
        return WorkflowRunBranch.For(workflowRunId);
    }

    private static bool IsWorkflowActive(IssueWorkflowStatus? workflow)
    {
        var status = workflow?.Workflow?.Status;
        return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "AwaitingApproval", StringComparison.OrdinalIgnoreCase);
    }

    private static object Unavailable(string reason, string message) => new { available = false, reason, message };

    private static object ToCleanupResponse(WorkspaceRemovalResult removal) => new
    {
        removed = removal.Removed,
        message = removal.Message,
        resources = new[]
        {
            new
            {
                type = "workspace",
                status = removal.Status,
                path = removal.Path,
                reason = removal.Reason,
            },
        },
    };
}
