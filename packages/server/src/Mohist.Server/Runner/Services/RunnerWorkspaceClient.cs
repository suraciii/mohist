using Mohist.Server.Contracts;
using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Grains;

namespace Mohist.Server.Runner.Services;

public interface IRunnerWorkspaceClient
{
    Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string hash, CancellationToken ct = default);
    Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
    Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string path, CancellationToken ct = default);
    Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default);
}

public sealed class RunnerWorkspaceClient(IRunnerControlTransport control, IGrainFactory grains) : IRunnerWorkspaceClient
{
    public async Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return null;
        return await ReadOrNullAsync<WorkspaceQueryParams, RunnerWorkspaceDiffResult?>(
            runnerId, "workspace.diff", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace)), ct);
    }

    public async Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return null;
        return await ReadOrNullAsync<WorkspaceQueryParams, RunnerWorkspaceCommitsResult?>(
            runnerId, "workspace.commits", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace)), ct);
    }

    public async Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string hash, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return null;
        return await ReadOrNullAsync<WorkspaceCommitDiffParams, RunnerWorkspaceCommitDiffResult?>(
            runnerId, "workspace.commit-diff", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), hash), ct);
    }

    public async Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return UnavailableStatus();
        try
        {
            return await control.SendRequestAsync<WorkspaceQueryParams, WorkspaceStatus>(
                runnerId, "workspace.status", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace)), ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return UnavailableStatus(); }
    }

    public async Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string path, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return UnavailableFile();
        try
        {
            return await control.SendRequestAsync<WorkspaceFileContentParams, RunnerWorkspaceFileContentResult>(
                runnerId, "workspace.file-content", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace), path), ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return UnavailableFile(); }
    }

    public async Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        var runnerId = await ResolveRunnerIdAsync(projectId, workflowRunId);
        if (runnerId is null) return UnavailableRemoval(workspace.Path);
        try
        {
            return await control.SendRequestAsync<WorkspaceQueryParams, WorkspaceRemovalResult>(
                runnerId, "workspace.remove", new(BuildQuery(projectId, workflowRunId, issueNumber, repository, workspace)), ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return UnavailableRemoval(workspace.Path); }
    }

    private async Task<TResult?> ReadOrNullAsync<TParams, TResult>(string runnerId, string method, TParams parameters, CancellationToken ct)
    {
        try { return await control.SendRequestAsync<TParams, TResult>(runnerId, method, parameters, ct: ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return default; }
    }

    private async Task<string?> ResolveRunnerIdAsync(string projectId, string workflowRunId)
    {
        var assigned = await grains.GetGrain<IWorkflowGrain>(workflowRunId).GetAssignedWorkerIdAsync();
        if (!string.IsNullOrWhiteSpace(assigned) && control.IsConnected(assigned)) return assigned;
        var eligible = await grains.GetGrain<IRunnerRegistryGrain>(RunnerRegistryKeys.Global).ListEligibleRunnersAsync(projectId);
        return eligible.FirstOrDefault(runner => control.IsConnected(runner.RunnerId))?.RunnerId;
    }

    private static WorkspaceStatus UnavailableStatus() => new() { Exists = false, Reason = "runner_unavailable" };
    private static RunnerWorkspaceFileContentResult UnavailableFile() => new(null, null, "runner_unavailable");
    private static WorkspaceRemovalResult UnavailableRemoval(string? path) =>
        new(false, "failed", path, "runner_unavailable", "Runner is not connected");

    private static RunnerWorkspaceQuery BuildQuery(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace)
    {
        var branch = string.IsNullOrWhiteSpace(workspace.Branch) ? WorkflowRunBranch.For(workflowRunId) : workspace.Branch;
        return new(workflowRunId, projectId, issueNumber, repository.Name, repository.GitUrl, workspace.Path, branch, repository.BaseBranch);
    }
}
