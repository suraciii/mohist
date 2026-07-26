using Microsoft.AspNetCore.SignalR;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Services.SignalR;

public interface IRunnerWorkspaceClient
{
    Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string hash, CancellationToken ct = default);
    Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string path, CancellationToken ct = default);
    Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
}

public sealed class RunnerWorkspaceClient : IRunnerWorkspaceClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IHubContext<RunnerHub> _hub;
    private readonly RunnerConnectionTracker _connections;
    private readonly IGrainFactory _grains;

    public RunnerWorkspaceClient(
        IHubContext<RunnerHub> hub,
        RunnerConnectionTracker connections,
        IGrainFactory grains)
    {
        _hub = hub;
        _connections = connections;
        _grains = grains;
    }

    public async Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null) return null;
        return await InvokeAsync<RunnerWorkspaceDiffResult>(connectionId, "GetDiff", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), ct);
    }

    public async Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null) return null;
        return await InvokeAsync<RunnerWorkspaceCommitsResult>(connectionId, "GetCommits", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), ct);
    }

    public async Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string hash, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null) return null;
        return await InvokeAsync<RunnerWorkspaceCommitDiffResult>(connectionId, "GetCommitDiff", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), hash, ct);
    }

    public async Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null)
            return new WorkspaceStatus { Exists = false, Reason = "runner_unavailable" };
        var result = await InvokeAsync<WorkspaceStatus>(connectionId, "GetWorkspaceStatus", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), ct);
        return result ?? new WorkspaceStatus { Exists = false, Reason = "workspace_removed" };
    }

    public async Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string path, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null)
            return new RunnerWorkspaceFileContentResult(null, null, "runner_unavailable");
        return await InvokeAsync<RunnerWorkspaceFileContentResult>(connectionId, "GetFileContent", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), path, ct)
            ?? new RunnerWorkspaceFileContentResult(null, null, "workspace_removed");
    }

    public async Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var connectionId = await ResolveConnectionIdAsync(projectId, workflowRunId);
        if (connectionId is null)
        {
            return new WorkspaceRemovalResult(
                Removed: false,
                Status: "failed",
                Path: workspace.Path,
                Reason: "runner_unavailable",
                Message: "Runner is not connected");
        }

        return await InvokeAsync<WorkspaceRemovalResult>(connectionId, "RemoveWorkspace", BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), ct)
            ?? new WorkspaceRemovalResult(false, "missing", workspace.Path, "workspace_missing", "Workspace already removed");
    }

    private async Task<string?> ResolveConnectionIdAsync(string projectId, string workflowRunId)
    {
        var workflow = _grains.GetGrain<IWorkflowGrain>(workflowRunId);
        var assignedWorkerId = await workflow.GetAssignedWorkerIdAsync();
        if (!string.IsNullOrWhiteSpace(assignedWorkerId))
        {
            var connectionId = _connections.GetConnectionId(assignedWorkerId);
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId;
        }

        var registry = _grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global);
        var eligible = await registry.ListEligibleRunnersAsync(projectId);
        foreach (var runner in eligible)
        {
            var connectionId = _connections.GetConnectionId(runner.RunnerId);
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId;
        }

        return null;
    }

    private async Task<T?> InvokeAsync<T>(string connectionId, string method, RunnerWorkspaceQuery query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        return await _hub.Clients.Client(connectionId).InvokeAsync<T?>(method, query, timeout.Token);
    }

    private async Task<T?> InvokeAsync<T>(string connectionId, string method, RunnerWorkspaceQuery query, string arg, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        return await _hub.Clients.Client(connectionId).InvokeAsync<T?>(method, query, arg, timeout.Token);
    }

    private static RunnerWorkspaceQuery BuildQuery(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace)
    {
        var branch = string.IsNullOrWhiteSpace(workspace.Branch)
            ? WorkflowRunBranch.For(workflowRunId)
            : workspace.Branch;
        return new RunnerWorkspaceQuery(
            WorkflowRunId: workflowRunId,
            ProjectId: projectId,
            IssueNumber: issueNumber,
            RepositoryName: repository.Name,
            GitUrl: repository.GitUrl,
            WorkspacePath: workspace.Path,
            Branch: branch,
            BaseBranch: repository.BaseBranch);
    }
}

public sealed record RunnerWorkspaceQuery(
    string? WorkflowRunId,
    string? ProjectId,
    int? IssueNumber,
    string? RepositoryName,
    string? GitUrl,
    string? WorkspacePath,
    string? Branch,
    string? BaseBranch);

public sealed record RunnerWorkspaceDiffResult(
    string Base,
    string Head,
    string MergeBase,
    int Ahead,
    int Behind,
    int CommitCount,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<DiffFile> Files);

public sealed record RunnerWorkspaceCommitsResult(
    string Base,
    string Head,
    string MergeBase,
    int Ahead,
    int Behind,
    int FilesChanged,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<GitCommit> Commits);

public sealed record RunnerWorkspaceCommitDiffResult(string Diff);

public sealed record RunnerWorkspaceFileContentResult(string? Base, string? Head, string? Reason = null);

public sealed record DiffFile(string File, int Additions, int Deletions, string Diff, bool IsBinary);

public sealed record GitCommit(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);

public sealed record WorkspaceRemovalResult(bool Removed, string Status, string? Path, string? Reason, string Message);
