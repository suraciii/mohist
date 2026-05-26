using System.Diagnostics;
using Mohist.Server.Workspace;
using Mohist.Server.Tests.Support;
using Xunit;

namespace Mohist.Server.Tests.Specs;

[Collection("MohistIntegration")]
public class WorkspaceCompatibilitySpecs
{
    private readonly HttpClient _client;
    private readonly MohistIntegrationFixture _fixture;

    public WorkspaceCompatibilitySpecs(MohistIntegrationFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task GitWorkspace_WhenBranchMissing_ReturnsCompatibleUnavailableResponses()
    {
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = $"workspace-{Guid.NewGuid():N}", path = Directory.GetCurrentDirectory(), baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Workspace issue", projectId = project.Id });

        var diff = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/diff?projectId={project.Id}");
        var commits = await _client.GetDataAsync<UnavailableDto>($"/api/issues/{issue.Number}/commits?projectId={project.Id}");
        var commitDiff = await _client.GetDataAsync<CommitDiffUnavailableDto>($"/api/issues/{issue.Number}/commits/deadbeef/diff?projectId={project.Id}");

        Assert.False(diff.Available);
        Assert.Equal("branch_missing", diff.Reason);
        Assert.False(commits.Available);
        Assert.Equal("branch_missing", commits.Reason);
        Assert.False(commitDiff.Available);
        Assert.Equal("deadbeef", commitDiff.Hash);
    }

    [Fact]
    public async Task WorktreeCleanup_WhenWorktreeExists_RemovesLocalWorktree()
    {
        using var repo = await CreateGitRepositoryAsync();
        var projectName = $"workspace-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = repo.Path, baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Cleanup issue", projectId = project.Id });
        var worktreePath = MohistWorkspaceLayout.IssueWorktreePath(_fixture.RunnerRoot, projectName, issue.Number);

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        await RunGitAsync(repo.Path, "worktree", "add", "-b", $"mo/issue-{issue.Number}", worktreePath, "main");
        Assert.True(Directory.Exists(worktreePath));

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/issues/{issue.Number}/cleanup?projectId={project.Id}");

        Assert.True(cleanup.Removed);
        Assert.Equal("Worktree removed", cleanup.Message);
        Assert.False(Directory.Exists(worktreePath));
    }

    [Fact]
    public async Task WorktreeCleanup_WhenLegacyWorktreeExists_RemovesLegacyWorktree()
    {
        using var repo = await CreateGitRepositoryAsync();
        var projectName = $"workspace-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = repo.Path, baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Legacy cleanup issue", projectId = project.Id });
        var legacyWorktreePath = MohistWorkspaceLayout.LegacyIssueWorktreePath(repo.Path, projectName, issue.Number);

        Directory.CreateDirectory(Path.GetDirectoryName(legacyWorktreePath)!);
        await RunGitAsync(repo.Path, "worktree", "add", "-b", $"mo/issue-{issue.Number}", legacyWorktreePath, "main");
        Assert.True(Directory.Exists(legacyWorktreePath));

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/issues/{issue.Number}/cleanup?projectId={project.Id}");

        Assert.True(cleanup.Removed);
        Assert.False(Directory.Exists(legacyWorktreePath));
    }

    [Fact]
    public async Task WorktreeCleanup_WhenWorktreeMissing_ReturnsIdempotentResult()
    {
        using var repo = await CreateGitRepositoryAsync();
        var projectName = $"workspace-{Guid.NewGuid():N}";
        var project = await _client.PostDataAsync<ProjectDto>("/api/projects", new { name = projectName, path = repo.Path, baseBranch = "main" });
        var issue = await _client.PostDataAsync<IssueDto>("/api/issues", new { title = "Missing cleanup issue", projectId = project.Id });

        var cleanup = await _client.PostDataAsync<CleanupDto>($"/api/issues/{issue.Number}/cleanup?projectId={project.Id}");

        Assert.False(cleanup.Removed);
        Assert.Equal("Worktree already removed", cleanup.Message);
        Assert.Contains(cleanup.Resources, r => r.Type == "worktree" && r.Status == "missing");
    }

    private static async Task<TempRepository> CreateGitRepositoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mohist-workspace-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "repo");
        Directory.CreateDirectory(path);
        await RunGitAsync(path, "init", "-b", "main");
        await RunGitAsync(path, "config", "user.email", "test@example.com");
        await RunGitAsync(path, "config", "user.name", "Mohist Test");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "test\n");
        await RunGitAsync(path, "add", "README.md");
        await RunGitAsync(path, "commit", "-m", "initial");
        return new TempRepository(root, path);
    }

    private static async Task RunGitAsync(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {await errorTask}{await outputTask}");
        }
    }

    private sealed record IssueDto(int Number);
    private sealed record ProjectDto(string Id);
    private sealed record UnavailableDto(bool Available, string Reason, string Message);
    private sealed record CommitDiffUnavailableDto(bool Available, string Reason, string Message, string Hash, string Diff);
    private sealed record CleanupDto(bool Removed, string Message, CleanupResourceDto[] Resources);
    private sealed record CleanupResourceDto(string Type, string Status, string? Path, string? Reason);

    private sealed class TempRepository : IDisposable
    {
        public TempRepository(string root, string path)
        {
            Root = root;
            Path = path;
        }

        public string Root { get; }
        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only; failed deletion should not hide the test result.
            }
        }
    }
}
