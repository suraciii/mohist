using Mohist.Server.Infrastructure.Workspace;

namespace Mohist.Server.SpecTests.Support;

public sealed class FakeGitService : IGitService
{
    private readonly List<RemoveWorkspaceCall> _removeWorkspaceCalls = [];

    public bool BranchExists { get; set; }
    public string? MergeBase { get; set; } = "merge-base";
    public (int Ahead, int Behind) AheadBehind { get; set; }
    public GitDiffResult Diff { get; set; } = new();
    public GitCommit[] Commits { get; set; } = [];
    public Dictionary<string, string?> CommitDiffs { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Branch, string FilePath), string?> FileContents { get; } = [];
    public WorkspaceStatus WorkspaceStatus { get; set; } = new() { Exists = false };
    public WorkspaceRemovalResult WorkspaceRemoval { get; set; } = new(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed");
    public Exception? Throw { get; set; }
    public IReadOnlyList<RemoveWorkspaceCall> RemoveWorkspaceCalls => _removeWorkspaceCalls;

    public void Reset()
    {
        BranchExists = false;
        MergeBase = "merge-base";
        AheadBehind = default;
        Diff = new GitDiffResult();
        Commits = [];
        CommitDiffs.Clear();
        FileContents.Clear();
        WorkspaceStatus = new WorkspaceStatus { Exists = false };
        WorkspaceRemoval = new WorkspaceRemovalResult(false, "missing", "/fake/workspace", "workspace_missing", "Workspace already removed");
        Throw = null;
        _removeWorkspaceCalls.Clear();
    }

    public Task<bool> BranchExistsAsync(string repoPath, string branchName)
    {
        MaybeThrow();
        return Task.FromResult(BranchExists);
    }

    public Task<string?> GetMergeBaseAsync(string repoPath, string baseBranch, string headBranch)
    {
        MaybeThrow();
        return Task.FromResult(MergeBase);
    }

    public Task<(int ahead, int behind)> GetAheadBehindAsync(string repoPath, string baseBranch, string headBranch)
    {
        MaybeThrow();
        return Task.FromResult((AheadBehind.Ahead, AheadBehind.Behind));
    }

    public Task<GitDiffResult> GetDiffAsync(string repoPath, string baseRef, string headRef)
    {
        MaybeThrow();
        return Task.FromResult(Diff);
    }

    public Task<GitCommit[]> GetCommitsAsync(string repoPath, string baseRef, string headRef)
    {
        MaybeThrow();
        return Task.FromResult(Commits);
    }

    public Task<string?> GetCommitDiffAsync(string repoPath, string hash)
    {
        MaybeThrow();
        return Task.FromResult(CommitDiffs.GetValueOrDefault(hash));
    }

    public Task<string?> GetFileContentAsync(string repoPath, string branch, string filePath)
    {
        MaybeThrow();
        return Task.FromResult(FileContents.GetValueOrDefault((branch, filePath)));
    }

    public Task<WorkspaceStatus> GetWorkspaceStatusAsync(string workspacePath, string baseBranch, string headBranch)
    {
        MaybeThrow();
        return Task.FromResult(WorkspaceStatus);
    }

    public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspacePath)
    {
        MaybeThrow();
        _removeWorkspaceCalls.Add(new RemoveWorkspaceCall(workspacePath));
        return Task.FromResult(WorkspaceRemoval);
    }

    private void MaybeThrow()
    {
        if (Throw is not null)
            throw Throw;
    }
}

public sealed record RemoveWorkspaceCall(string WorkspacePath);
