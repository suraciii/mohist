using System.Diagnostics;
using System.Text.Json;
using Mohist.Runner.Actions;
using Xunit;

namespace Mohist.Runner.Tests.Specs;

public class GitAndOpenSpecActionSpecs
{
    [Fact]
    public async Task MergeReady_InCleanRepository_Passes()
    {
        using var temp = new TempDir();
        await InitRepositoryAsync(temp.Path);

        var result = await new MergeReadyAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "check", "mohist/merge-ready", new { baseBranch = "main" }));

        Assert.Equal("success", result.Status);
        using var document = JsonDocument.Parse(result.Output!);
        Assert.True(document.RootElement.GetProperty("canMerge").GetBoolean());
    }

    [Fact]
    public async Task OpenSpecSync_CopiesSpecFiles()
    {
        using var temp = new TempDir();
        var changeDir = Path.Combine(temp.Path, "openspec", "changes", "1-test");
        var specDir = Path.Combine(changeDir, "specs", "search");
        Directory.CreateDirectory(specDir);
        await File.WriteAllTextAsync(Path.Combine(specDir, "spec.md"), "Requirement");

        var result = await new OpenSpecSyncAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/openspec-sync", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.True(File.Exists(Path.Combine(temp.Path, "specs", "search", "spec.md")));
    }

    [Fact]
    public async Task OpenSpecSync_MissingSpecsDirectory_Fails()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "openspec", "changes", "1-test"));

        var result = await new OpenSpecSyncAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/openspec-sync", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("failure", result.Status);
    }

    [Fact]
    public async Task ArchiveChange_MovesChangeDirectoryToArchive()
    {
        using var temp = new TempDir();
        var changeDir = Path.Combine(temp.Path, "openspec", "changes", "1-test");
        Directory.CreateDirectory(changeDir);
        await File.WriteAllTextAsync(Path.Combine(changeDir, "proposal.md"), "proposal");

        var result = await new ArchiveChangeAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/archive-change", new { changeDir = "openspec/changes/1-test" }));

        Assert.Equal("success", result.Status);
        Assert.False(Directory.Exists(changeDir));
        var archiveDir = Path.Combine(temp.Path, "openspec", "changes", "archive");
        Assert.Contains(Directory.EnumerateDirectories(archiveDir), path => path.EndsWith("1-test"));
    }

    [Fact]
    public async Task ArchiveChange_MissingChangeDir_Fails()
    {
        using var temp = new TempDir();

        var result = await new ArchiveChangeAction().ExecuteAsync(
            SpecHelpers.Context(temp.Path, "task", "mohist/archive-change", new { changeDir = "openspec/changes/missing" }));

        Assert.Equal("failure", result.Status);
    }

    [Fact]
    public async Task Merge_SquashesIssueBranchIntoBaseBranch()
    {
        using var repo = new TempDir();
        await InitRepositoryAsync(repo.Path);
        await RunGitAsync(repo.Path, "checkout", "-b", "mo/issue-1");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "feature.txt"), "delivered");
        await RunGitAsync(repo.Path, "add", ".");
        await RunGitAsync(repo.Path, "commit", "-m", "issue work");
        var issueHead = await GitOutputAsync(repo.Path, "rev-parse", "HEAD");
        await RunGitAsync(repo.Path, "checkout", "main");

        var result = await new MergeAction().ExecuteAsync(
            SpecHelpers.Context(repo.Path, "task", "mohist/merge", new
            {
                source = "mo/issue-1",
                target = "main",
                strategy = "squash",
                message = "Complete issue #1"
            }));

        Assert.Equal("success", result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Path, "feature.txt")));
        Assert.Equal("main", await GitOutputAsync(repo.Path, "branch", "--show-current"));
        Assert.NotEqual(issueHead, await GitOutputAsync(repo.Path, "rev-parse", "HEAD"));
        using var document = JsonDocument.Parse(result.Output!);
        Assert.Equal("main", document.RootElement.GetProperty("target").GetString());
        Assert.Equal("mo/issue-1", document.RootElement.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("landedSha").GetString()));
    }

    [Fact]
    public async Task Merge_WhenRunningFromIssueWorktree_MergesIntoProjectPath()
    {
        using var repo = new TempDir();
        using var runnerRoot = new TempDir();
        await InitRepositoryAsync(repo.Path);
        var worktreePath = Path.Combine(runnerRoot.Path, "issue-1");
        await RunGitAsync(repo.Path, "worktree", "add", "-b", "mo/issue-1", worktreePath, "main");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "feature.txt"), "delivered");
        await RunGitAsync(worktreePath, "add", ".");
        await RunGitAsync(worktreePath, "commit", "-m", "issue work");
        Directory.CreateDirectory(Path.Combine(worktreePath, "specs"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "specs", "feature.md"), "synced");

        var result = await new MergeAction().ExecuteAsync(
            SpecHelpers.Context(worktreePath, "task", "mohist/merge", new
            {
                source = "mo/issue-1",
                target = "main",
                strategy = "squash",
                message = "Complete issue #1"
            }, new Dictionary<string, object?>
            {
                ["project"] = new { path = repo.Path }
            }));

        Assert.Equal("success", result.Status);
        Assert.True(File.Exists(Path.Combine(repo.Path, "feature.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "specs", "feature.md")));
        Assert.Equal("main", await GitOutputAsync(repo.Path, "branch", "--show-current"));
        Assert.Equal("mo/issue-1", await GitOutputAsync(worktreePath, "branch", "--show-current"));
    }

    private static async Task InitRepositoryAsync(string path)
    {
        await RunGitAsync(path, "init", "-b", "main");
        await RunGitAsync(path, "config", "user.email", "mohist@example.test");
        await RunGitAsync(path, "config", "user.name", "Mohist Test");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "hello");
        await RunGitAsync(path, "add", ".");
        await RunGitAsync(path, "commit", "-m", "initial");
    }

    private static async Task RunGitAsync(string workDir, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed");
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace)
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;

    private static async Task<string> GitOutputAsync(string workDir, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = string.Join(" ", args.Select(Quote)),
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output.Trim();
    }
}
