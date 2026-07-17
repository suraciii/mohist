using Mohist.Server.Infrastructure.Workspace;
using Mohist.Server.Runner.Grains;
using Mohist.Server.Runner.Services.SignalR;
using Mohist.Server.Workflow.Domain.Run;

namespace Mohist.Server.SpecTests.Support;

public sealed class FakeRunnerWorkspaceClient : IRunnerWorkspaceClient
{
    private readonly object _gate = new();
    private readonly List<RemoveWorkspaceCall> _removeWorkspaceCalls = [];

    public WorkspaceStatus WorkspaceStatus { get; set; } = new() { Exists = false, Reason = "workspace_removed" };
    public RunnerWorkspaceDiffResult? Diff { get; set; }
    public RunnerWorkspaceCommitsResult? Commits { get; set; }
    public Dictionary<string, RunnerWorkspaceCommitDiffResult?> CommitDiffs { get; } = new(StringComparer.Ordinal);
    public RunnerWorkspaceFileContentResult FileContent { get; set; } = new(null, null, "workspace_removed");
    public WorkspaceRemovalResult WorkspaceRemoval { get; set; } = new(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed");
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
        WorkspaceRemoval = new WorkspaceRemovalResult(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed");
        Throw = null;
        LastBaseBranch = null;
        lock (_gate)
        {
            _removeWorkspaceCalls.Clear();
        }
    }

    public Task<RunnerWorkspaceDiffResult?> GetDiffAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, string baseBranch, CancellationToken ct = default)
    {
        MaybeThrow();
        LastBaseBranch = baseBranch;
        return Task.FromResult(Diff);
    }

    public Task<RunnerWorkspaceCommitsResult?> GetCommitsAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, string baseBranch, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(Commits);
    }

    public Task<RunnerWorkspaceCommitDiffResult?> GetCommitDiffAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, string baseBranch, string hash, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(CommitDiffs.GetValueOrDefault(hash));
    }

    public Task<WorkspaceStatus> GetWorkspaceStatusAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, string baseBranch, CancellationToken ct = default)
    {
        MaybeThrow();
        LastBaseBranch = baseBranch;
        return Task.FromResult(WorkspaceStatus);
    }

    public Task<RunnerWorkspaceFileContentResult> GetFileContentAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, string baseBranch, string path, CancellationToken ct = default)
    {
        MaybeThrow();
        return Task.FromResult(FileContent);
    }

    public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string projectId, string workflowRunId, WorkspaceIdentity workspace, CancellationToken ct = default)
    {
        MaybeThrow();
        lock (_gate)
        {
            _removeWorkspaceCalls.Add(new RemoveWorkspaceCall(workspace.Path));
        }
        return Task.FromResult(WorkspaceRemoval);
    }

    private void MaybeThrow()
    {
        if (Throw is not null)
            throw Throw;
    }
}
