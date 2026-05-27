using Mohist.Server.Workspace;

namespace Mohist.Server.Tests.Support;

public sealed class FakeGitService : IGitService
{
    private readonly List<RemoveWorktreeCall> _removeWorktreeCalls = [];

    public bool BranchExists { get; set; }
    public string? MergeBase { get; set; } = "merge-base";
    public (int Ahead, int Behind) AheadBehind { get; set; }
    public GitDiffResult Diff { get; set; } = new();
    public GitCommit[] Commits { get; set; } = [];
    public Dictionary<string, string?> CommitDiffs { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Branch, string FilePath), string?> FileContents { get; } = [];
    public WorkspaceStatus WorktreeStatus { get; set; } = new() { Exists = false };
    public WorktreeRemovalResult WorktreeRemoval { get; set; } = new(false, "missing", "/fake/worktree", "worktree_missing", "Worktree already removed");
    public IReadOnlyList<RemoveWorktreeCall> RemoveWorktreeCalls => _removeWorktreeCalls;

    public void Reset()
    {
        BranchExists = false;
        MergeBase = "merge-base";
        AheadBehind = default;
        Diff = new GitDiffResult();
        Commits = [];
        CommitDiffs.Clear();
        FileContents.Clear();
        WorktreeStatus = new WorkspaceStatus { Exists = false };
        WorktreeRemoval = new WorktreeRemovalResult(false, "missing", "/fake/worktree", "worktree_missing", "Worktree already removed");
        _removeWorktreeCalls.Clear();
    }

    public Task<bool> BranchExistsAsync(string repoPath, string branchName) => Task.FromResult(BranchExists);

    public Task<string?> GetMergeBaseAsync(string repoPath, string baseBranch, string headBranch) => Task.FromResult(MergeBase);

    public Task<(int ahead, int behind)> GetAheadBehindAsync(string repoPath, string baseBranch, string headBranch)
        => Task.FromResult((AheadBehind.Ahead, AheadBehind.Behind));

    public Task<GitDiffResult> GetDiffAsync(string repoPath, string baseRef, string headRef) => Task.FromResult(Diff);

    public Task<GitCommit[]> GetCommitsAsync(string repoPath, string baseRef, string headRef) => Task.FromResult(Commits);

    public Task<string?> GetCommitDiffAsync(string repoPath, string hash)
        => Task.FromResult(CommitDiffs.GetValueOrDefault(hash));

    public Task<string?> GetFileContentAsync(string repoPath, string branch, string filePath)
        => Task.FromResult(FileContents.GetValueOrDefault((branch, filePath)));

    public Task<WorkspaceStatus> GetWorktreeStatusAsync(string projectPath, string projectName, int issueNumber, string baseBranch)
        => Task.FromResult(WorktreeStatus);

    public Task<WorktreeRemovalResult> RemoveWorktreeAsync(string projectPath, string projectName, int issueNumber)
    {
        _removeWorktreeCalls.Add(new RemoveWorktreeCall(projectPath, projectName, issueNumber));
        return Task.FromResult(WorktreeRemoval);
    }
}

public sealed record RemoveWorktreeCall(string ProjectPath, string ProjectName, int IssueNumber);
