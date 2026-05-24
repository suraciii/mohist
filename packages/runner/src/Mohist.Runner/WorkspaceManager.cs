using System.Text.Json;
using Mohist.Runner.Actions;

namespace Mohist.Runner;

public class WorkspaceManager : IWorkspaceManager
{
    private readonly ILogger<WorkspaceManager> _log;
    private readonly string _runnerRoot;

    public WorkspaceManager(ILogger<WorkspaceManager> log, string? runnerRoot = null)
    {
        _log = log;
        _runnerRoot = runnerRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mohist",
            "projects");
    }

    public async Task<WorkspaceInfo> EnsureAsync(Dictionary<string, JsonElement?> variables, CancellationToken ct)
    {
        return await EnsureAsync(new VariableBag(variables), ct);
    }

    public async Task<WorkspaceInfo> EnsureAsync(VariableBag variables, CancellationToken ct)
    {
        var workspacePath = ResolveExistingWorkspace(variables);
        if (workspacePath is not null)
            return new WorkspaceInfo(workspacePath, null, ResolveChangeDir(variables, workspacePath));

        var projectPath = ResolveString(variables, "project.path");
        var issueNumber = ResolveIssueNumber(variables);
        if (!string.IsNullOrWhiteSpace(projectPath) && issueNumber is > 0)
        {
            var projectName = ResolveString(variables, "project.name")
                ?? ResolveString(variables, "project.id")
                ?? "default";
            var baseBranch = ResolveString(variables, "project.baseBranch")
                ?? ResolveString(variables, "project.defaultBranch")
                ?? "main";
            var branch = BranchName(issueNumber.Value);
            workspacePath = await EnsureGitWorktreeAsync(projectPath, projectName, issueNumber.Value, baseBranch, branch, ct);
            var gitChangeDir = ResolveChangeDir(variables, workspacePath);
            CreateArtifactDirectories(gitChangeDir);
            return new WorkspaceInfo(workspacePath, branch, gitChangeDir);
        }

        workspacePath = ResolveProjectWorktree(variables, ct);
        var changeDir = ResolveChangeDir(variables, workspacePath);
        CreateDirectories(workspacePath, changeDir);

        return new WorkspaceInfo(workspacePath, ResolveBranch(variables), changeDir);
    }

    private static string? ResolveExistingWorkspace(VariableBag variables)
    {
        var path = variables.String("workspace.path");
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path : null;
    }

    private string ResolveProjectWorktree(VariableBag variables, CancellationToken ct)
    {
        var projectId = ResolveString(variables, "project.id") ?? "default";
        var issueNumber = ResolveIssueNumber(variables) ?? 0;
        var issueDir = issueNumber > 0 ? $"issue-{issueNumber}" : "worktree";

        var path = Path.Combine(_runnerRoot, projectId, "worktrees", issueDir);

        _log.LogInformation("Resolved workspace path: {Path}", path);
        return path;
    }

    private static string? ResolveBranch(VariableBag variables)
    {
        var issueNumber = ResolveIssueNumber(variables) ?? 0;
        return issueNumber is > 0 ? BranchName(issueNumber) : null;
    }

    private static string? ResolveChangeDir(VariableBag variables, string workspacePath)
    {
        var changeDir = ResolveString(variables, "artifacts.changeDir");
        if (string.IsNullOrWhiteSpace(changeDir)) return null;

        var fullPath = Path.Combine(workspacePath, changeDir!);
        return fullPath;
    }

    private static void CreateDirectories(string workspace, string? changeDir)
    {
        Directory.CreateDirectory(workspace);
        CreateArtifactDirectories(changeDir);
    }

    private static void CreateArtifactDirectories(string? changeDir)
    {
        if (changeDir is not null)
        {
            Directory.CreateDirectory(changeDir);
            Directory.CreateDirectory(Path.Combine(changeDir, "specs"));
        }
    }

    private static string? ResolveString(VariableBag variables, string path) => variables.String(path);

    private static int? ResolveIssueNumber(VariableBag variables)
    {
        var issueNumberStr = ResolveString(variables, "issue.number");
        return int.TryParse(issueNumberStr, out var n) ? n : null;
    }

    private async Task<string> EnsureGitWorktreeAsync(string projectPath, string projectName, int issueNumber, string baseBranch, string branch, CancellationToken ct)
    {
        projectPath = Path.GetFullPath(projectPath);
        var worktreePath = Path.Combine(_runnerRoot, Slug(projectName), "worktrees", $"issue-{issueNumber}");
        if (Directory.Exists(worktreePath))
            return worktreePath;

        var gitDir = await GitCommand.RunAsync(projectPath, ["rev-parse", "--git-dir"], ct);
        if (!gitDir.Success)
            throw new InvalidOperationException($"Project is not a git repository: {projectPath}");

        var head = await GitCommand.RunAsync(projectPath, ["rev-parse", "HEAD"], ct);
        if (!head.Success)
            throw new InvalidOperationException("Repository has no commits. Create an initial commit before starting an issue.");

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        var branchExists = await GitCommand.RunAsync(projectPath, ["rev-parse", "--verify", branch], ct);
        var args = branchExists.Success
            ? new[] { "worktree", "add", worktreePath, branch }
            : ["worktree", "add", "-b", branch, worktreePath, baseBranch];

        _log.LogInformation("Creating worktree {WorktreePath} for {Branch} from {BaseBranch}", worktreePath, branch, baseBranch);
        var result = await GitCommand.RunAsync(projectPath, args, ct);
        if (!result.Success)
            throw new InvalidOperationException($"Failed to create worktree: {result.CombinedOutput}");

        return worktreePath;
    }

    private static string BranchName(int issueNumber) => $"mo/issue-{issueNumber}";

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "project" : slug;
    }
}
