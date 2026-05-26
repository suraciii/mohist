using System.Diagnostics;
using Mohist.Runner;

namespace Mohist.Server.Workspace;

public class GitService : IGitService
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(10);
    private readonly string _runnerRoot;

    public GitService(string? runnerRoot = null)
    {
        _runnerRoot = runnerRoot is null
            ? MohistWorkspaceLayout.DefaultRunnerRoot()
            : Path.GetFullPath(runnerRoot);
    }

    public async Task<bool> BranchExistsAsync(string repoPath, string branchName)
    {
        var result = await RunGitAsync(repoPath, "rev-parse", "--verify", $"refs/heads/{branchName}");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    public async Task<string?> GetMergeBaseAsync(string repoPath, string baseBranch, string headBranch)
    {
        var result = await RunGitAsync(repoPath, "merge-base", baseBranch, headBranch);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    public async Task<(int ahead, int behind)> GetAheadBehindAsync(string repoPath, string baseBranch, string headBranch)
    {
        var result = await RunGitAsync(repoPath, "rev-list", "--left-right", "--count", $"{baseBranch}...{headBranch}");
        if (result.ExitCode != 0) return (0, 0);
        var parts = result.Output.Trim().Split('\t');
        if (parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead))
            return (ahead, behind);
        return (0, 0);
    }

    public async Task<GitDiffResult> GetDiffAsync(string repoPath, string baseRef, string headRef)
    {
        var numstat = await RunGitAsync(repoPath, "diff", $"{baseRef}...{headRef}", "--numstat");
        var fullDiff = await RunGitAsync(repoPath, "diff", $"{baseRef}...{headRef}");
        var patches = SplitDiffByFile(fullDiff.Output);

        var files = new List<DiffFile>();
        var totalAdditions = 0;
        var totalDeletions = 0;

        foreach (var line in numstat.Output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var isBinary = parts[0] == "-" && parts[1] == "-";
            var add = isBinary ? 0 : int.TryParse(parts[0], out var a) ? a : 0;
            var del = isBinary ? 0 : int.TryParse(parts[1], out var d) ? d : 0;
            var path = parts[2];
            files.Add(new DiffFile(path, add, del, patches.GetValueOrDefault(path, ""), isBinary));
            totalAdditions += add;
            totalDeletions += del;
        }

        return new GitDiffResult
        {
            Files = files,
            TotalAdditions = totalAdditions,
            TotalDeletions = totalDeletions,
            RawDiff = fullDiff.Output,
        };
    }

    public async Task<GitCommit[]> GetCommitsAsync(string repoPath, string baseRef, string headRef)
    {
        var format = "%H\t%h\t%s\t%an\t%ad\t%N";
        var result = await RunGitAsync(repoPath, "log", $"{baseRef}...{headRef}", $"--format={format}", "--date=iso");
        if (result.ExitCode != 0) return [];

        return result.Output.Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 5)
            .Select(p => new GitCommit(p[0], p[1], p[2], p[3], p[4], []))
            .ToArray();
    }

    public async Task<string?> GetCommitDiffAsync(string repoPath, string hash)
    {
        var result = await RunGitAsync(repoPath, "show", "--format=", "--patch", hash);
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task<string?> GetFileContentAsync(string repoPath, string branch, string filePath)
    {
        var result = await RunGitAsync(repoPath, "show", $"{branch}:{filePath}");
        return result.ExitCode == 0 ? result.Output : null;
    }

    public async Task<WorkspaceStatus> GetWorktreeStatusAsync(string projectPath, string projectName, int issueNumber, string baseBranch)
    {
        var branchName = $"mo/issue-{issueNumber}";
        var worktreePath = GetExistingIssueWorktreePath(projectPath, projectName, issueNumber);

        if (worktreePath is null)
            return new WorkspaceStatus { Exists = false };

        var exists = await BranchExistsAsync(projectPath, branchName);
        if (!exists)
            return new WorkspaceStatus { Exists = false };

        var (ahead, behind) = await GetAheadBehindAsync(projectPath, baseBranch, branchName);
        var rebaseResult = await RunGitAsync(projectPath, "rebase", "--show-current-patch");
        var rebaseInProgress = rebaseResult.ExitCode == 0;

        string[] conflicts = [];
        if (rebaseInProgress)
        {
            var statusResult = await RunGitAsync(projectPath, "diff", "--name-only", "--diff-filter=U");
            conflicts = statusResult.Output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        }

        return new WorkspaceStatus
        {
            Exists = true,
            Branch = branchName,
            BaseBranch = baseBranch,
            Ahead = ahead,
            Behind = behind,
            RebaseInProgress = rebaseInProgress,
            ConflictingFiles = conflicts,
        };
    }

    public async Task<WorktreeRemovalResult> RemoveWorktreeAsync(string projectPath, string projectName, int issueNumber)
    {
        var worktreePath = GetIssueWorktreePath(projectName, issueNumber);
        var existingWorktreePath = GetExistingIssueWorktreePath(projectPath, projectName, issueNumber);
        if (existingWorktreePath is null)
        {
            return new WorktreeRemovalResult(
                Removed: false,
                Status: "missing",
                Path: worktreePath,
                Reason: "worktree_missing",
                Message: "Worktree already removed");
        }

        var result = await RunGitAsync(projectPath, "worktree", "remove", "--force", existingWorktreePath);
        if (result.ExitCode == 0 || !Directory.Exists(existingWorktreePath))
        {
            return new WorktreeRemovalResult(
                Removed: true,
                Status: "removed",
                Path: existingWorktreePath,
                Reason: null,
                Message: "Worktree removed");
        }

        var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
        return new WorktreeRemovalResult(
            Removed: false,
            Status: "failed",
            Path: existingWorktreePath,
            Reason: "git_worktree_remove_failed",
            Message: string.IsNullOrWhiteSpace(error) ? "Failed to remove worktree" : error);
    }

    private string GetIssueWorktreePath(string projectName, int issueNumber)
        => MohistWorkspaceLayout.IssueWorktreePath(_runnerRoot, projectName, issueNumber);

    private static string GetLegacyIssueWorktreePath(string projectPath, string projectName, int issueNumber)
        => MohistWorkspaceLayout.LegacyIssueWorktreePath(projectPath, projectName, issueNumber);

    private string? GetExistingIssueWorktreePath(string projectPath, string projectName, int issueNumber)
    {
        var worktreePath = GetIssueWorktreePath(projectName, issueNumber);
        if (Directory.Exists(worktreePath)) return worktreePath;

        var legacyWorktreePath = GetLegacyIssueWorktreePath(projectPath, projectName, issueNumber);
        return Directory.Exists(legacyWorktreePath) ? legacyWorktreePath : null;
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunGitAsync(string workingDir, string command, params string[] args)
    {
        var psi = new ProcessStartInfo("git", [command, ..args])
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process == null) return ("", "", -1);

        using var timeout = new CancellationTokenSource(GitCommandTimeout);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort: the caller receives a timeout-like git failure below.
            }

            return ("", $"git {command} timed out after {GitCommandTimeout.TotalSeconds:0}s", 124);
        }
        return (await outputTask, await errorTask, process.ExitCode);
    }

    private static Dictionary<string, string> SplitDiffByFile(string diff)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(diff)) return result;

        string? currentPath = null;
        var current = new List<string>();
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                currentPath = parts.Length >= 4 && parts[3].StartsWith("b/", StringComparison.Ordinal)
                    ? parts[3][2..]
                    : null;
            }
            current.Add(line);
        }
        Flush();
        return result;

        void Flush()
        {
            if (currentPath is not null && current.Count > 0)
                result[currentPath] = string.Join('\n', current) + "\n";
            current.Clear();
        }
    }
}

public interface IGitService
{
    Task<bool> BranchExistsAsync(string repoPath, string branchName);
    Task<string?> GetMergeBaseAsync(string repoPath, string baseBranch, string headBranch);
    Task<(int ahead, int behind)> GetAheadBehindAsync(string repoPath, string baseBranch, string headBranch);
    Task<GitDiffResult> GetDiffAsync(string repoPath, string baseRef, string headRef);
    Task<GitCommit[]> GetCommitsAsync(string repoPath, string baseRef, string headRef);
    Task<string?> GetCommitDiffAsync(string repoPath, string hash);
    Task<string?> GetFileContentAsync(string repoPath, string branch, string filePath);
    Task<WorkspaceStatus> GetWorktreeStatusAsync(string projectPath, string projectName, int issueNumber, string baseBranch);
    Task<WorktreeRemovalResult> RemoveWorktreeAsync(string projectPath, string projectName, int issueNumber);
}

public class GitDiffResult
{
    public List<DiffFile> Files { get; set; } = [];
    public int TotalAdditions { get; set; }
    public int TotalDeletions { get; set; }
    public string RawDiff { get; set; } = "";
}

public record DiffFile(string File, int Additions, int Deletions, string Diff, bool IsBinary);
public record GitCommit(string Hash, string ShortHash, string Message, string Author, string Date, string[] Files);
public record WorktreeRemovalResult(bool Removed, string Status, string? Path, string? Reason, string Message);
