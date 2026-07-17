using Microsoft.AspNetCore.Http;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;

namespace Mohist.Server.Api;

public static class WorkspaceRoutes
{
    public static WebApplication MapWorkspaceRoutes(this WebApplication app)
    {
        var issues = app.MapGroup("/api/projects/{projectRef}/issues/{number:int}")
            .AddEndpointFilter<ProjectResolutionEndpointFilter>();

        issues.MapGet("/diff", async (
            HttpContext context, int number,
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            var prepared = await PrepareWorkspaceQueryAsync(querier, issue);
            if (prepared.Unavailable is not null) return ApiResults.Ok(prepared.Unavailable);

            try
            {
                var unavailable = await EnsureRunnerWorkspaceAvailableAsync(
                    runnerWorkspace,
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (unavailable is not null) return ApiResults.Ok(unavailable);

                var result = await runnerWorkspace.GetDiffAsync(
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (result is null)
                    return ApiResults.Ok(Unavailable("git_error", "Runner did not return diff data"));

                var patches = result.Files.Select(f => new { path = f.File, diff = f.Diff }).ToArray();
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
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            var prepared = await PrepareWorkspaceQueryAsync(querier, issue);
            if (prepared.Unavailable is not null) return ApiResults.Ok(prepared.Unavailable);

            try
            {
                var unavailable = await EnsureRunnerWorkspaceAvailableAsync(
                    runnerWorkspace,
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (unavailable is not null) return ApiResults.Ok(unavailable);

                var result = await runnerWorkspace.GetCommitsAsync(
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (result is null)
                    return ApiResults.Ok(Unavailable("git_error", "Runner did not return commit data"));

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
            catch (Exception ex)
            {
                return ApiResults.Ok(Unavailable("git_error", ex.Message));
            }
        });

        issues.MapGet("/commits/{hash}/diff", async (
            HttpContext context, int number, string hash,
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            var prepared = await PrepareWorkspaceQueryAsync(querier, issue);
            if (prepared.Unavailable is not null)
                return ApiResults.Ok(ToCommitDiffUnavailable(prepared.Unavailable, hash));

            try
            {
                var unavailable = await EnsureRunnerWorkspaceAvailableAsync(
                    runnerWorkspace,
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (unavailable is not null) return ApiResults.Ok(ToCommitDiffUnavailable(unavailable, hash));

                var result = await runnerWorkspace.GetCommitDiffAsync(
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    hash,
                    context.RequestAborted);
                if (result is null)
                    return ApiResults.Ok(new { available = false, reason = "git_error", message = $"Commit {hash} not found", hash, diff = "" });

                return ApiResults.Ok(new { available = true, reason = (string?)null, hash, diff = result.Diff });
            }
            catch (Exception ex)
            {
                return ApiResults.Ok(new { available = false, reason = "git_error", message = ex.Message, hash, diff = "" });
            }
        });

        issues.MapGet("/workspace-status", async (
            HttpContext context, int number,
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            var prepared = await PrepareWorkspaceQueryAsync(querier, issue);
            if (prepared.Unavailable is not null)
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = prepared.Unavailable.Reason });

            try
            {
                var unavailable = await EnsureRunnerWorkspaceAvailableAsync(
                    runnerWorkspace,
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (unavailable is not null)
                    return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = unavailable.Reason });

                var result = await runnerWorkspace.GetWorkspaceStatusAsync(
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                return ApiResults.Ok(result);
            }
            catch (Exception)
            {
                return ApiResults.Ok(new WorkspaceStatus { Exists = false, Reason = "git_error" });
            }
        });

        issues.MapGet("/file-content", async (
            HttpContext context, int number, string path,
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            var prepared = await PrepareWorkspaceQueryAsync(querier, issue);
            if (prepared.Unavailable is not null)
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = prepared.Unavailable.Reason });

            try
            {
                var unavailable = await EnsureRunnerWorkspaceAvailableAsync(
                    runnerWorkspace,
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    context.RequestAborted);
                if (unavailable is not null)
                    return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = unavailable.Reason });

                var result = await runnerWorkspace.GetFileContentAsync(
                    pid,
                    issue.WorkflowRunId!,
                    issue.Number,
                    prepared.Repository!,
                    prepared.Workspace!,
                    path,
                    context.RequestAborted);
                return ApiResults.Ok(new { @base = result.Base, head = result.Head, reason = result.Reason });
            }
            catch (Exception)
            {
                return ApiResults.Ok(new { @base = (string?)null, head = (string?)null, reason = "git_error" });
            }
        });

        issues.MapPost("/cleanup", async (
            HttpContext context,
            int number,
            IGrainFactory grains,
            IRunnerWorkspaceClient runnerWorkspace,
            WorkflowQuerier querier,
            WorkflowActivityQuerier projection,
            IssueQuerier issuesQuery) =>
        {
            var pid = context.GetResolvedProject().Id;
            var issue = await issuesQuery.GetAsync(pid, number);
            if (issue is null) return ApiResults.NotFound("Issue not found");
            if (CheckRepositoryConfig(issue) is { } repoError) return repoError;

            var grain = grains.GetGrain<IIssueGrain>(GrainKey.Issue(new IssueKey(pid, number)));
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

            if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
                return ApiResults.Conflict("No workflow workspace to clean", "workspace_missing");

            var workspace = await ResolveWorkspaceAsync(querier, issue);
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path))
                return ApiResults.Conflict("No workflow workspace to clean", "workspace_missing");
            var repository = await querier.GetRepositoryContextAsync(issue.WorkflowRunId);
            if (repository is null)
                return ApiResults.Conflict("No workflow repository context to clean", "missing_repository_context");

            var removal = await runnerWorkspace.RemoveWorkspaceAsync(
                pid,
                issue.WorkflowRunId,
                issue.Number,
                repository,
                workspace,
                context.RequestAborted);
            if (removal.Status == "failed")
                return ApiResults.Conflict(removal.Message, removal.Reason ?? "workspace_cleanup_failed", removal);

            return ApiResults.Ok(ToCleanupResponse(removal));
        });

        return app;
    }

    private static async Task<PreparedWorkspaceQuery> PrepareWorkspaceQueryAsync(WorkflowQuerier querier, IssueReadModel issue)
    {
        if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
            return PreparedWorkspaceQuery.UnavailableResult(Unavailable("not_started", "Issue has no active workflow"));

        var workspace = await ResolveWorkspaceAsync(querier, issue);
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.Path))
            return PreparedWorkspaceQuery.UnavailableResult(Unavailable("workspace_removed", "The workflow workspace is not available"));

        if (WorkspaceHeadOrNull(workspace, issue.WorkflowRunId) is null)
            return PreparedWorkspaceQuery.UnavailableResult(Unavailable("branch_missing", "Workspace head branch is not recorded for this run"));

        var repository = await querier.GetRepositoryContextAsync(issue.WorkflowRunId);
        if (repository is null)
            return PreparedWorkspaceQuery.UnavailableResult(Unavailable("missing_repository_context", "The workflow repository context is not available"));

        return PreparedWorkspaceQuery.Ready(workspace, repository);
    }

    private static async Task<WorkspaceUnavailable?> EnsureRunnerWorkspaceAvailableAsync(
        IRunnerWorkspaceClient runnerWorkspace,
        string projectId,
        string workflowRunId,
        int issueNumber,
        WorkflowRepositoryContext repository,
        WorkspaceIdentity workspace,
        CancellationToken ct)
    {
        var status = await runnerWorkspace.GetWorkspaceStatusAsync(projectId, workflowRunId, issueNumber, repository, workspace, ct);
        if (status.Exists && !string.Equals(status.Reason, "branch_missing", StringComparison.Ordinal))
            return null;

        var reason = string.IsNullOrWhiteSpace(status.Reason) ? "workspace_removed" : status.Reason;
        return Unavailable(reason, MessageForUnavailableReason(reason));
    }

    private static async Task<WorkspaceIdentity?> ResolveWorkspaceAsync(WorkflowQuerier querier, IssueReadModel issue)
    {
        if (string.IsNullOrWhiteSpace(issue.WorkflowRunId))
            return null;
        return await querier.GetWorkspaceAsync(issue.WorkflowRunId);
    }

    /// <summary>
    /// Returns the workspace's recorded head ref. The runner prepares the
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

    private static WorkspaceUnavailable Unavailable(string reason, string message) => new(false, reason, message);

    private static string MessageForUnavailableReason(string reason) => reason switch
    {
        "not_started" => "Issue has no active workflow",
        "runner_unavailable" => "Runner is not connected",
        "branch_missing" => "Workspace branch not found in workspace",
        "workspace_removed" => "The workflow workspace is not available",
        "missing_repository_context" => "The workflow repository context is not available",
        _ => "Workspace unavailable",
    };

    private static object ToCommitDiffUnavailable(WorkspaceUnavailable unavailable, string hash) =>
        new { available = false, reason = unavailable.Reason, message = unavailable.Message, hash, diff = "" };

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

    private sealed record PreparedWorkspaceQuery(WorkspaceIdentity? Workspace, WorkflowRepositoryContext? Repository, WorkspaceUnavailable? Unavailable)
    {
        public static PreparedWorkspaceQuery Ready(WorkspaceIdentity workspace, WorkflowRepositoryContext repository) => new(workspace, repository, null);
        public static PreparedWorkspaceQuery UnavailableResult(WorkspaceUnavailable unavailable) => new(null, null, unavailable);
    }
}

public sealed record WorkspaceUnavailable(bool Available, string Reason, string Message);
