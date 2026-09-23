using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services;
using Mohist.Server.Contracts;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.TestSupport;

public sealed class FakeRunnerWorkspaceClient : IRunnerWorkspaceClient
{
    private readonly object _gate = new();
    private readonly List<RemoveWorkspaceCall> _removeWorkspaceCalls = [];

    public WorkspaceStatus WorkspaceStatus { get; set; } = new() { Exists = false, Reason = "workspace_removed" };
    public RunnerWorkspaceDiffResult? Diff { get; set; }
    public RunnerWorkspaceCommitsResult? Commits { get; set; }
    public Dictionary<string, RunnerWorkspaceCommitDiffResult?> CommitDiffs { get; } = new(StringComparer.Ordinal);
    public RunnerWorkspaceFileContentResult FileContent { get; set; } = new(null, null, "workspace_removed");
    public WorkspaceRemovalResult WorkspaceRemoval { get; set; } = new(false, "already_absent", "/fake/workspace", "workspace_missing", "Workspace was already absent");
    public Func<Task<WorkspaceRemovalResult>>? RemoveWorkspaceHandler { get; set; }
    public WorkspaceInspectionResult WorkspaceInspection { get; set; } = new("already_absent", null);
    public Exception? Throw { get; set; }
    public string? LastBaseBranch { get; private set; }
    public IReadOnlyList<RemoveWorkspaceCall> RemoveWorkspaceCalls
    {
        get { lock (_gate) return _removeWorkspaceCalls.ToList(); }
    }

    public void Reset()
    {
        WorkspaceStatus = new WorkspaceStatus { Exists = false, Reason = "workspace_removed" };
        Diff = null;
        Commits = null;
        CommitDiffs.Clear();
        FileContent = new RunnerWorkspaceFileContentResult(null, null, "workspace_removed");
        WorkspaceRemoval = new WorkspaceRemovalResult(false, "already_absent", "/fake/workspace", "workspace_missing", "Workspace was already absent");
        RemoveWorkspaceHandler = null;
        WorkspaceInspection = new WorkspaceInspectionResult("already_absent", null);
        Throw = null;
        LastBaseBranch = null;
        lock (_gate)
        {
            _removeWorkspaceCalls.Clear();
        }
    }

    public Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        LastBaseBranch = repository.BaseBranch;
        return Task.FromResult(Diff);
    }

    public Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(Commits);
    }

    public Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string hash, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(CommitDiffs.GetValueOrDefault(hash));
    }

    public Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        LastBaseBranch = repository.BaseBranch;
        return Task.FromResult(WorkspaceStatus);
    }

    public Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, string path, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(FileContent);
    }

    public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        lock (_gate)
        {
            _removeWorkspaceCalls.Add(new RemoveWorkspaceCall(workspace.Path));
        }
        return RemoveWorkspaceHandler?.Invoke() ?? Task.FromResult(WorkspaceRemoval);
    }

    public Task<WorkspaceInspectionResult> InspectWorkspaceAsync(string projectId, string workflowRunId, int issueNumber, WorkflowRepositoryContext repository, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(WorkspaceInspection);
    }

    private void MaybeThrow()
    {
        if (Throw is not null)
            throw Throw;
    }
}

public sealed record RemoveWorkspaceCall(string WorkspacePath);
