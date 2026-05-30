using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Queries;
using Mohist.Server.Project.Queries;
using Mohist.Server.Runner.SignalR;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Projection;
using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/issues/{number:int}");

        var workflow = issues.MapGroup("/workflow");

        workflow.MapGet("/diff", async (
            int number, string? projectId,
            IGrainFactory grains, IHubContext<RunnerHub> hub, RunnerConnectionTracker tracker,
            IssueQueryService issuesQuery, ProjectQueryService projectsQuery,
            CancellationToken ct) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var runId = issue.WorkflowRunId;
            if (string.IsNullOrEmpty(runId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow" });

            var (runnerId, connId) = await ResolveRunnerAsync(runId, grains, tracker);
            if (runnerId is null || connId is null)
                return ApiResults.Ok(new { available = false, reason = "no_runner", message = "No active runner for this workflow" });

            try
            {
                var result = await hub.Clients.Client(connId)
                    .InvokeAsync<RunnerDiffResponse>("GetDiff", number, QueryTimeout, ct);

                if (result is null)
                    return ApiResults.Ok(new { available = false, reason = "empty", message = "No diff available" });

                return ApiResults.Ok(new
                {
                    available = true,
                    reason = (string?)null,
                    @base = result.Base,
                    head = result.Head,
                    mergeBase = result.MergeBase,
                    ahead = result.Ahead,
                    behind = result.Behind,
                    canFastForward = result.Behind == 0,
                    comparison = "merge-base",
                    summary = new { filesChanged = result.Files.Count, commits = result.CommitCount, additions = result.TotalAdditions, deletions = result.TotalDeletions },
                    files = result.Files,
                });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out" });
            }
        });

        workflow.MapGet("/commits", async (
            int number, string? projectId,
            IGrainFactory grains, IHubContext<RunnerHub> hub, RunnerConnectionTracker tracker,
            IssueQueryService issuesQuery, ProjectQueryService projectsQuery,
            CancellationToken ct) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var runId = issue.WorkflowRunId;
            if (string.IsNullOrEmpty(runId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow" });

            var (runnerId, connId) = await ResolveRunnerAsync(runId, grains, tracker);
            if (runnerId is null || connId is null)
                return ApiResults.Ok(new { available = false, reason = "no_runner", message = "No active runner for this workflow" });

            try
            {
                var result = await hub.Clients.Client(connId)
                    .InvokeAsync<RunnerCommitsResponse>("GetCommits", number, QueryTimeout, ct);

                if (result is null)
                    return ApiResults.Ok(new { available = false, reason = "empty", message = "No commits available" });

                return ApiResults.Ok(new
                {
                    available = true,
                    reason = (string?)null,
                    @base = result.Base,
                    head = result.Head,
                    mergeBase = result.MergeBase,
                    ahead = result.Ahead,
                    behind = result.Behind,
                    canFastForward = result.Behind == 0,
                    comparison = "merge-base",
                    summary = new { filesChanged = result.FilesChanged, commits = result.Commits.Count, additions = result.TotalAdditions, deletions = result.TotalDeletions },
                    commits = result.Commits,
                });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out" });
            }
        });

        workflow.MapGet("/commits/{hash}/diff", async (
            int number, string hash, string? projectId,
            IGrainFactory grains, IHubContext<RunnerHub> hub, RunnerConnectionTracker tracker,
            IssueQueryService issuesQuery, ProjectQueryService projectsQuery,
            CancellationToken ct) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var runId = issue.WorkflowRunId;
            if (string.IsNullOrEmpty(runId))
                return ApiResults.Ok(new { available = false, reason = "not_started", message = "Issue has no active workflow", hash, diff = "" });

            var (runnerId, connId) = await ResolveRunnerAsync(runId, grains, tracker);
            if (runnerId is null || connId is null)
                return ApiResults.Ok(new { available = false, reason = "no_runner", message = "No active runner for this workflow", hash, diff = "" });

            try
            {
                var result = await hub.Clients.Client(connId)
                    .InvokeAsync<RunnerCommitDiffResponse>("GetCommitDiff", number, hash, QueryTimeout, ct);

                if (result is null)
                    return ApiResults.NotFound($"Commit {hash} not found");

                return ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff = result.Diff });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { available = false, reason = "timeout", message = "Runner query timed out", hash, diff = "" });
            }
        });

        workflow.MapGet("/worktree-status", async (
            int number, string? projectId,
            IGrainFactory grains, IHubContext<RunnerHub> hub, RunnerConnectionTracker tracker,
            IssueQueryService issuesQuery, ProjectQueryService projectsQuery,
            CancellationToken ct) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var runId = issue.WorkflowRunId;
            if (string.IsNullOrEmpty(runId))
                return ApiResults.Ok(new { exists = false });

            var (runnerId, connId) = await ResolveRunnerAsync(runId, grains, tracker);
            if (runnerId is null || connId is null)
                return ApiResults.Ok(new { exists = false, reason = "no_runner" });

            try
            {
                var result = await hub.Clients.Client(connId)
                    .InvokeAsync<RunnerWorktreeStatusResponse>("GetWorktreeStatus", number, QueryTimeout, ct);

                return result is null
                    ? ApiResults.Ok(new { exists = false })
                    : ApiResults.Ok(result);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { exists = false, reason = "timeout" });
            }
        });

        workflow.MapGet("/file-content", async (
            int number, string path, string? projectId,
            IGrainFactory grains, IHubContext<RunnerHub> hub, RunnerConnectionTracker tracker,
            IssueQueryService issuesQuery, ProjectQueryService projectsQuery,
            CancellationToken ct) =>
        {
            var (pid, issue) = await ResolveIssueAsync(number, projectId, projectsQuery, issuesQuery);
            if (pid is null) return ApiResults.BadRequest("No active project");
            if (issue is null) return ApiResults.NotFound("Issue not found");

            var runId = issue.WorkflowRunId;
            if (string.IsNullOrEmpty(runId))
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null });

            var (runnerId, connId) = await ResolveRunnerAsync(runId, grains, tracker);
            if (runnerId is null || connId is null)
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null });

            try
            {
                var result = await hub.Clients.Client(connId)
                    .InvokeAsync<RunnerFileContentResponse>("GetFileContent", number, path, QueryTimeout, ct);

                return result is null
                    ? ApiResults.Ok(new { @base = (string?)null, head = (string?)null })
                    : ApiResults.Ok(new { @base = result.Base, head = result.Head });
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null });
            }
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

    private static async Task<(string? RunnerId, string? ConnectionId)> ResolveRunnerAsync(
        string runId, IGrainFactory grains, RunnerConnectionTracker tracker)
    {
        var workflowGrain = grains.GetGrain<IWorkflowGrain>(runId);
        var runnerId = await workflowGrain.GetAssignedRunnerIdAsync();
        if (string.IsNullOrEmpty(runnerId))
            return (null, null);

        var connId = tracker.GetConnectionId(runnerId);
        return (runnerId, connId);
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

public record RunnerDiffResponse(
    string Base, string Head, string? MergeBase,
    int Ahead, int Behind, int CommitCount,
    int TotalAdditions, int TotalDeletions,
    List<RunnerDiffFile> Files);

public record RunnerDiffFile(string File, int Additions, int Deletions, string Diff, bool IsBinary);

public record RunnerCommitsResponse(
    string Base, string Head, string? MergeBase,
    int Ahead, int Behind,
    int FilesChanged, int TotalAdditions, int TotalDeletions,
    List<RunnerCommit> Commits);

public record RunnerCommit(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);

public record RunnerCommitDiffResponse(string Diff);

public record RunnerWorktreeStatusResponse(
    bool Exists, string? Branch, string? BaseBranch,
    int Ahead, int Behind, bool RebaseInProgress, string[] ConflictingFiles);

public record RunnerFileContentResponse(string? Base, string? Head);
